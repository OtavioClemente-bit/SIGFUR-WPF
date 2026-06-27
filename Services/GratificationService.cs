using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class GratificationService
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly MilitaryRepository _repository;
    private readonly LogService _log;

    public GratificationService(AppPaths paths, JsonFileService json, MilitaryRepository repository, LogService log)
    {
        _paths = paths;
        _json = json;
        _repository = repository;
        _log = log;
        EnsureTemplatesInstalled();
    }

    private void EnsureTemplatesInstalled()
    {
        try
        {
            Directory.CreateDirectory(_paths.GratificationTemplatesDirectory);
            CopyShippedTemplate("Grat Rep Diex.docx");
            CopyShippedTemplate("Mapa_Grat_Rep__4ciaPE._att.docx");
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync("Falha ao preparar modelos da Gratificação de Representação.", ex);
        }
    }

    private void CopyShippedTemplate(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "Gratificacao", fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", "Gratificacao", fileName)),
            Path.Combine(AppContext.BaseDirectory, "templates", "docs", fileName)
        };
        var source = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(source)) return;
        var destination = Path.Combine(_paths.GratificationTemplatesDirectory, fileName);
        if (!File.Exists(destination) || new FileInfo(destination).Length != new FileInfo(source).Length)
            File.Copy(source, destination, true);
    }

    public async Task<GratificationSettings> LoadSettingsAsync()
    {
        var current = await _json.LoadAsync<GratificationSettings>(_paths.GratificationSettingsFile);
        if (current is not null) return NormalizeSettings(current);
        var migrated = await MigrateLegacyAsync();
        await SaveSettingsAsync(migrated);
        return migrated;
    }

    public Task SaveSettingsAsync(GratificationSettings settings)
        => _json.SaveAsync(_paths.GratificationSettingsFile, NormalizeSettings(settings));

    public GratificationPeriodInfo CalculatePeriod(DateTime start, DateTime end)
    {
        if (end <= start)
            return new GratificationPeriodInfo { IsValid = false, Error = "O retorno deve ser posterior à saída.", Start = start, End = end };

        var duration = end - start;
        var fullDays = (int)Math.Floor(duration.TotalHours / 24d);
        var remainder = duration - TimeSpan.FromDays(fullDays);

        // Decreto nº 11.002/2022, art. 4º, § 1º:
        // cada período igual ou superior a 8h e inferior a 24h conta como 1 dia.
        var days = fullDays + (remainder >= TimeSpan.FromHours(8) ? 1 : 0);
        return new GratificationPeriodInfo { IsValid = true, Start = start, End = end, Duration = duration, IndemnifiableDays = days };
    }

    public static bool TryCombine(DateTime? date, string? time, out DateTime result)
    {
        result = default;
        if (date is null) return false;
        var raw = (time ?? string.Empty).Trim().Replace('h', ':').Replace('H', ':');
        if (raw.Length == 4 && raw.All(char.IsDigit)) raw = raw.Insert(2, ":");
        if (!TimeSpan.TryParseExact(raw, ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var parsed)
            && !TimeSpan.TryParse(raw, PtBr, out parsed)) return false;
        result = date.Value.Date + parsed;
        return true;
    }

    public async Task<List<GratificationParticipant>> BuildParticipantsAsync(IEnumerable<MilitaryRecord> military, int days, CancellationToken cancellationToken = default)
    {
        var result = new List<GratificationParticipant>();
        foreach (var item in military.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var salary = await _repository.GetSalaryByRankAsync(item.Rank, cancellationToken);
            result.Add(new GratificationParticipant { Military = item, Salary = salary, Days = Math.Max(0, days) });
        }
        return result;
    }

    public async Task<List<GratificationEffectiveRow>> BuildEffectiveRowsAsync(GratificationSettings settings, int days, CancellationToken cancellationToken = default)
    {
        var ranks = new[]
        {
            "General de Exército", "General de Divisão", "General de Brigada", "Coronel", "Tenente Coronel", "Major", "Capitão",
            "1º Tenente", "2º Tenente", "Aspirante", "Subtenente", "1º Sargento", "2º Sargento", "3º Sargento",
            "Cabo Efetivo Profissional", "Soldado Efetivo Profissional", "Soldado Efetivo Variável"
        };
        var rows = new List<GratificationEffectiveRow>();
        foreach (var rank in ranks)
        {
            var canonical = MilitaryRankService.Canonicalize(rank);
            var shortRank = MilitaryRankService.ShortName(rank);
            var quantity = FindQuantity(settings.EffectiveByRank, canonical, shortRank);
            rows.Add(new GratificationEffectiveRow
            {
                Rank = canonical,
                Salary = await _repository.GetSalaryByRankAsync(canonical, cancellationToken),
                Quantity = quantity,
                Days = Math.Max(0, days)
            });
        }
        return rows;
    }

    public BulletinRenderResult BuildBulletin(GratificationSettings settings, GratificationPeriodInfo period, IReadOnlyList<GratificationParticipant> participants)
    {
        if (string.IsNullOrWhiteSpace(settings.Destination)) throw new InvalidOperationException("Informe o local de destino.");
        if (string.IsNullOrWhiteSpace(settings.Purpose)) throw new InvalidOperationException("Informe a finalidade do deslocamento.");
        if (string.IsNullOrWhiteSpace(settings.BulletinReference)) throw new InvalidOperationException("Informe o boletim ou documento que autorizou o deslocamento.");
        if (!period.IsValid) throw new InvalidOperationException(period.Error);
        if (period.IndemnifiableDays <= 0) throw new InvalidOperationException("O período informado é inferior a 8 horas e não gera dia indenizável para Gratificação de Representação.");
        if (participants.Count == 0) throw new InvalidOperationException("Adicione ao menos um militar.");

        var result = new BulletinRenderResult();
        var text = new StringBuilder();
        var legalBasis = "Decreto nº 11.002, de 17 MAR 22, especialmente os arts. 4º, 5º e 8º, e Portaria – C Ex nº 1.887, de 7 DEZ 22 (EB10-N-08.003)";
        var totalGeneral = participants.Sum(x => x.Total);

        text.Append("Com fundamento no ").Append(legalBasis)
            .Append(", e considerando a autorização publicada no ").Append(settings.BulletinReference.Trim())
            .Append(", seja realizado o saque da Gratificação de Representação 2% (normal) em favor dos militares abaixo relacionados, ")
            .Append("referente ao deslocamento a serviço para ").Append(settings.Destination.Trim())
            .Append(", no período de ").Append(FormatDateTimeAbbreviated(period.Start)).Append(" a ").Append(FormatDateTimeAbbreviated(period.End))
            .Append(", com a finalidade de ").Append(settings.Purpose.Trim()).AppendLine(".")
            .Append("Para conferência do lançamento, o cálculo observou ").Append(period.RuleText).Append(" ")
            .Append("A parcela não deve ser acumulada com diárias, quando houver coincidência de fato gerador.").AppendLine()
            .Append("Valor total da publicação: ").Append(totalGeneral.ToString("C2", PtBr)).Append(" (")
            .Append(NumberToWordsService.Convert(totalGeneral, currency: true)).AppendLine(").")
            .AppendLine();

        foreach (var participant in participants)
        {
            var rank = MilitaryRankService.ShortName(participant.Military.Rank);
            text.Append(rank).Append(' ');
            AppendHighlightedName(text, result.BoldRanges, participant.Military.Name.ToUpper(PtBr), participant.Military.WarName.ToUpper(PtBr));
            text.AppendLine();
            text.Append("Prec-CP ").Append(participant.Military.PrecCp).Append(" CPF ").Append(MilitaryFormatting.FormatCpf(participant.Military.Cpf)).AppendLine();
            text.Append("Período: ").Append(FormatDateTimeAbbreviated(period.Start)).Append(" a ").Append(FormatDateTimeAbbreviated(period.End)).AppendLine(";");
            text.Append("Quantidade de dias: ").Append(period.IndemnifiableDays).Append(" (")
                .Append(NumberToWordsService.Convert(period.IndemnifiableDays, currency: false)).AppendLine(") dias;");
            text.Append("Valor diário 2%: ").Append(participant.DailyRate.ToString("C2", PtBr)).AppendLine(";");
            text.Append("Valor solicitado: ").Append(participant.Total.ToString("C2", PtBr)).Append(" (")
                .Append(NumberToWordsService.Convert(participant.Total, currency: true)).AppendLine(").");
            text.AppendLine();
        }
        result.Text = text.ToString().TrimEnd();
        return result;
    }

    public async Task ExportCsvAsync(string path, IReadOnlyList<GratificationParticipant> rows, GratificationPeriodInfo period, CancellationToken cancellationToken = default)
    {
        static string Csv(string? value) => '"' + (value ?? string.Empty).Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + '"';
        var lines = new List<string>
        {
            "NOME COMPLETO;POSTO/GRADUAÇÃO;CPF;PREC-CP;SOLDO;2% AO DIA;INÍCIO;RETORNO;DURAÇÃO;DIAS;TOTAL"
        };
        lines.AddRange(rows.Select(x => string.Join(';',
            Csv(NameHighlightHelper.PlainDisplay(x.Military.Name, x.Military.WarName)), Csv(MilitaryRankService.ShortName(x.Military.Rank)), Csv(MilitaryFormatting.FormatCpf(x.Military.Cpf)), Csv(x.Military.PrecCp),
            x.Salary.ToString("0.00", CultureInfo.InvariantCulture), x.DailyRate.ToString("0.00", CultureInfo.InvariantCulture),
            Csv(FormatDateTimeAbbreviated(period.Start)), Csv(FormatDateTimeAbbreviated(period.End)), Csv(period.DurationText),
            period.IndemnifiableDays.ToString(CultureInfo.InvariantCulture), x.Total.ToString("0.00", CultureInfo.InvariantCulture))));
        lines.Add(string.Join(';', Csv("TOTAL GERAL"), "", "", "", "", "", "", "", "", "", rows.Sum(x => x.Total).ToString("0.00", CultureInfo.InvariantCulture)));
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true), cancellationToken);
    }

    public async Task ExportXlsxAsync(string path, IReadOnlyList<GratificationParticipant> rows, GratificationPeriodInfo period, CancellationToken cancellationToken = default)
        => await Task.Run(() => WriteXlsx(path, rows, period), cancellationToken);

    public async Task<string> GenerateDiexAsync(string path, GratificationSettings settings, GratificationPeriodInfo period, IReadOnlyList<GratificationEffectiveRow> rows, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            var active = ValidateEffectiveRows(period, rows);
            var mapping = BuildTemplateMapping(settings, period, active);
            var template = Path.Combine(_paths.GratificationTemplatesDirectory, "Grat Rep Diex.docx");
            if (!TryGenerateFromTemplate(template, path, mapping))
            {
                var paragraphs = BuildRequestParagraphs(settings, period, active, "DIEx — SOLICITAÇÃO DE GRATIFICAÇÃO DE REPRESENTAÇÃO (2%)");
                WriteDocx(path, "SOLICITAÇÃO DE GRATIFICAÇÃO DE REPRESENTAÇÃO (2%)", paragraphs,
                    ["P/G", "EFETIVO", "SOLDO", "2%/DIA", "DIAS", "SUBTOTAL"],
                    active.Select(x => new[] { x.ShortRank, x.Quantity.ToString(PtBr), x.SalaryText, x.DailyRateText, x.Days.ToString(PtBr), x.SubtotalText }).ToList());
            }
        }, cancellationToken);
        return path;
    }

    public async Task<string> GenerateMapAsync(string path, GratificationSettings settings, GratificationPeriodInfo period, IReadOnlyList<GratificationEffectiveRow> rows, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            var active = ValidateEffectiveRows(period, rows);
            var mapping = BuildTemplateMapping(settings, period, active);
            var template = Path.Combine(_paths.GratificationTemplatesDirectory, "Mapa_Grat_Rep__4ciaPE._att.docx");
            if (!TryGenerateFromTemplate(template, path, mapping))
            {
                var paragraphs = BuildRequestParagraphs(settings, period, active, "MAPA DE GRATIFICAÇÃO DE REPRESENTAÇÃO (2%)");
                WriteDocx(path, "MAPA DE GRATIFICAÇÃO DE REPRESENTAÇÃO (2%)", paragraphs,
                    ["POSTO/GRADUAÇÃO", "EFETIVO", "VALOR UNITÁRIO", "DIAS", "VALOR TOTAL"],
                    active.Select(x => new[] { x.ShortRank, x.Quantity.ToString(PtBr), x.DailyRateText, x.Days.ToString(PtBr), x.SubtotalText }).ToList(),
                    footer: ["TOTAL", active.Sum(x => x.Quantity).ToString(PtBr), "", period.IndemnifiableDays.ToString(PtBr), active.Sum(x => x.Subtotal).ToString("C2", PtBr)]);
            }
        }, cancellationToken);
        return path;
    }

    public string DefaultOutputDirectory
    {
        get
        {
            var path = Path.Combine(_paths.GeneratedDocumentsDirectory, "Gratificacao_Representacao");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string FormatDateTimeAbbreviated(DateTime value)
    {
        var months = new[] { "JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ" };
        return $"{value:dd} {months[value.Month - 1]} {value:yy}, às {value:HH}h{value:mm}";
    }

    private static List<GratificationEffectiveRow> ValidateEffectiveRows(GratificationPeriodInfo period, IReadOnlyList<GratificationEffectiveRow> rows)
    {
        if (!period.IsValid) throw new InvalidOperationException(period.Error);
        if (period.IndemnifiableDays <= 0) throw new InvalidOperationException("O período informado não gera dia indenizável.");
        var active = rows.Where(x => x.Quantity > 0).ToList();
        if (active.Count == 0) throw new InvalidOperationException("Informe o efetivo de ao menos um posto/graduação.");
        return active;
    }

    private static List<string> BuildRequestParagraphs(GratificationSettings settings, GratificationPeriodInfo period, IReadOnlyList<GratificationEffectiveRow> rows, string heading)
    {
        var total = rows.Sum(x => x.Subtotal);
        return
        [
            heading,
            $"Organização Militar: {settings.RequestOrganization}",
            $"Natureza: {settings.RequestNature}",
            $"Descrição: {settings.RequestDescription}",
            $"Documento autorizador: {settings.RequestAuthorizingDocument}",
            $"Enquadramento: {settings.RequestLegalBasis}",
            $"Local: {settings.RequestLocation}",
            $"Período: {FormatDateTimeAbbreviated(period.Start)} a {FormatDateTimeAbbreviated(period.End)} — {period.DurationText}",
            $"Dias indenizáveis: {period.IndemnifiableDays} ({NumberToWordsService.Convert(period.IndemnifiableDays, false)})",
            $"Relação nominal: {settings.RequestBulletin}",
            $"Efetivo total: {rows.Sum(x => x.Quantity)} militar(es)",
            $"Valor total: {total.ToString("C2", PtBr)} ({NumberToWordsService.Convert(total, true)})",
            $"Contato: {settings.RequestContact} | RITEX: {settings.RequestRitex} | E-mail: {settings.RequestEmail}",
            $"{settings.RequestCity}, {DateTime.Today:dd 'de' MMMM 'de' yyyy}.",
            settings.RequestAuthority
        ];
    }

    private static void AppendHighlightedName(StringBuilder text, ICollection<BulletinBoldRange> boldRanges, string fullName, string warName)
    {
        foreach (var segment in NameHighlightHelper.BuildSegments(fullName, warName))
        {
            var start = text.Length;
            text.Append(segment.Text);
            if (segment.IsBold && segment.Text.Length > 0) boldRanges.Add(new BulletinBoldRange(start, segment.Text.Length));
        }
    }

    private async Task<GratificationSettings> MigrateLegacyAsync()
    {
        var settings = new GratificationSettings();
        var node = await _json.LoadNodeAsync(_paths.LegacyGratificationSettingsFile) as JsonObject;
        if (node is null) return settings;
        string S(string key, string fallback) => node[key]?.ToString()?.Trim() is { Length: > 0 } value ? value : fallback;
        settings.Destination = S("local", settings.Destination);
        settings.Purpose = S("finalidade", settings.Purpose);
        settings.DepartureDate = ParseDate(S("saida", string.Empty)) ?? settings.DepartureDate;
        settings.DepartureTime = S("hora_saida", settings.DepartureTime);
        settings.ReturnDate = ParseDate(S("retorno", string.Empty)) ?? settings.ReturnDate;
        settings.ReturnTime = S("hora_retorno", settings.ReturnTime);
        settings.BulletinReference = S("bi", settings.BulletinReference);
        settings.SisbolSubject = S("grat2_assunto_sisbol", settings.SisbolSubject);
        settings.SisbolSpecificCode = S("grat2_codigo_sisbol", string.Empty);
        settings.Search = S("busca", string.Empty);
        settings.RequestNature = S("sol_natureza", settings.RequestNature);
        settings.RequestDescription = S("sol_descricao", settings.RequestDescription);
        settings.RequestAuthorizingDocument = S("sol_doc_aut", settings.RequestAuthorizingDocument);
        settings.RequestLegalBasis = S("sol_enquadramento", settings.RequestLegalBasis);
        settings.RequestLocation = S("sol_local", settings.RequestLocation);
        settings.RequestBulletin = S("sol_bi", settings.RequestBulletin);
        settings.RequestContact = S("sol_contato", settings.RequestContact);
        settings.RequestRitex = S("sol_ritex", settings.RequestRitex);
        settings.RequestEmail = S("sol_email", settings.RequestEmail);
        settings.RequestAuthority = S("sol_assinatura", settings.RequestAuthority);
        settings.RequestOrganization = S("sol_om", settings.RequestOrganization);
        settings.RequestCity = S("sol_cidade_quartel", settings.RequestCity);
        if (int.TryParse(node["sol_dias"]?.ToString(), out var manualDays) && manualDays > 0)
            settings.RequestManualDays = manualDays;
        var start = ParseDtg(S("sol_per_ini", string.Empty));
        var end = ParseDtg(S("sol_per_fim", string.Empty));
        if (start is not null) { settings.RequestStartDate = start.Value.Date; settings.RequestStartTime = start.Value.ToString("HH:mm"); }
        if (end is not null) { settings.RequestEndDate = end.Value.Date; settings.RequestEndTime = end.Value.ToString("HH:mm"); }
        foreach (var pair in LegacyEffectiveKeys)
        {
            if (int.TryParse(node[pair.Key]?.ToString(), out var quantity) && quantity > 0)
                settings.EffectiveByRank[pair.Value] = quantity;
        }
        return NormalizeSettings(settings);
    }

    private static GratificationSettings NormalizeSettings(GratificationSettings settings)
    {
        settings.SelectedMilitaryIds ??= [];
        settings.EffectiveByRank = settings.EffectiveByRank is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(settings.EffectiveByRank, StringComparer.OrdinalIgnoreCase);
        if (settings.DepartureDate == default) settings.DepartureDate = DateTime.Today;
        if (settings.ReturnDate == default) settings.ReturnDate = settings.DepartureDate;
        if (settings.RequestStartDate == default) settings.RequestStartDate = DateTime.Today;
        if (settings.RequestEndDate == default) settings.RequestEndDate = settings.RequestStartDate;
        settings.RequestOrganization = string.IsNullOrWhiteSpace(settings.RequestOrganization) ? "4ª Cia PE" : settings.RequestOrganization.Trim();
        settings.RequestManualDays = Math.Max(0, settings.RequestManualDays);
        return settings;
    }

    private static int FindQuantity(IReadOnlyDictionary<string, int> values, params string[] keys)
    {
        foreach (var pair in values)
            if (keys.Any(key => MilitaryRankService.Normalize(pair.Key).Equals(MilitaryRankService.Normalize(key), StringComparison.OrdinalIgnoreCase)))
                return Math.Max(0, pair.Value);
        return 0;
    }

    private static DateTime? ParseDate(string? value)
    {
        foreach (var format in new[] { "dd/MM/yyyy", "yyyy-MM-dd", "dd-MM-yyyy" })
            if (DateTime.TryParseExact(value, format, PtBr, DateTimeStyles.None, out var parsed)) return parsed;
        return null;
    }

    private static DateTime? ParseDtg(string? value)
    {
        var raw = (value ?? string.Empty).Trim().ToUpperInvariant();
        var months = new Dictionary<string, int> { ["JAN"] = 1, ["FEV"] = 2, ["MAR"] = 3, ["ABR"] = 4, ["MAI"] = 5, ["JUN"] = 6, ["JUL"] = 7, ["AGO"] = 8, ["SET"] = 9, ["OUT"] = 10, ["NOV"] = 11, ["DEZ"] = 12 };
        if (raw.Length != 11) return null;
        if (!int.TryParse(raw[..2], out var day) || !int.TryParse(raw.Substring(2, 2), out var hour) || !int.TryParse(raw.Substring(4, 2), out var minute) || !months.TryGetValue(raw.Substring(6, 3), out var month) || !int.TryParse(raw.Substring(9, 2), out var yy)) return null;
        try { return new DateTime(2000 + yy, month, day, hour, minute, 0); } catch { return null; }
    }

    private static readonly IReadOnlyDictionary<string, string> LegacyEffectiveKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sol_eff_GENEX"] = "General de Exército", ["sol_eff_GENDIV"] = "General de Divisão", ["sol_eff_GENBDA"] = "General de Brigada",
        ["sol_eff_CEL"] = "Coronel", ["sol_eff_TC"] = "Tenente Coronel", ["sol_eff_MAJ"] = "Major", ["sol_eff_CAP"] = "Capitão",
        ["sol_eff_1TEN"] = "1º Tenente", ["sol_eff_2TEN"] = "2º Tenente", ["sol_eff_ASP"] = "Aspirante", ["sol_eff_ST"] = "Subtenente",
        ["sol_eff_1SGT"] = "1º Sargento", ["sol_eff_2SGT"] = "2º Sargento", ["sol_eff_3SGT"] = "3º Sargento", ["sol_eff_CB"] = "Cabo Efetivo Profissional",
        ["sol_eff_SDEP"] = "Soldado Efetivo Profissional", ["sol_eff_SDEV"] = "Soldado Efetivo Variável"
    };

    private static Dictionary<string, string> BuildTemplateMapping(GratificationSettings settings, GratificationPeriodInfo period, IReadOnlyList<GratificationEffectiveRow> rows)
    {
        var total = rows.Sum(x => x.Subtotal);
        var effectiveSummary = string.Join(", ", rows.Where(x => x.Quantity > 0).Select(x => $"{x.Quantity} {x.ShortRank}"));
        var dateText = DateTime.Today.ToString("dd 'de' MMMM 'de' yyyy", PtBr);
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{{NATUREZA}}"] = settings.RequestNature,
            ["{{DESCRICAO}}"] = settings.RequestDescription,
            ["{{DOC_AUT}}"] = settings.RequestAuthorizingDocument,
            ["{{ENQUADRAMENTO}}"] = settings.RequestLegalBasis,
            ["{{LOCAL}}"] = settings.RequestLocation,
            ["{{PERIODO_INI}}"] = FormatDateTimeAbbreviated(period.Start),
            ["{{PERIODO_FIM}}"] = FormatDateTimeAbbreviated(period.End),
            ["{{DIAS}}"] = period.IndemnifiableDays.ToString(PtBr),
            ["{{DIAS_EXT}}"] = NumberToWordsService.Convert(period.IndemnifiableDays, false),
            ["{{CALCULO_DIAS}}"] = period.RuleText,
            ["{{BI}}"] = settings.RequestBulletin,
            ["{{TOTAL_GERAL}}"] = total.ToString("C2", PtBr),
            ["{{TOTAL_EXTENSO}}"] = NumberToWordsService.Convert(total, true),
            ["{{RESUMO_EFETIVO}}"] = effectiveSummary,
            ["{{CONTATO}}"] = $"{settings.RequestContact} | RITEX {settings.RequestRitex} | {settings.RequestEmail}",
            ["{{ASSINATURA}}"] = settings.RequestAuthority,
            ["{{COMANDANTE}}"] = settings.RequestAuthority,
            ["{{OM}}"] = settings.RequestOrganization,
            ["{{ORGANIZACAO_MILITAR}}"] = settings.RequestOrganization,
            ["{{CIDADE_QUARTEL}}"] = settings.RequestCity,
            ["{{DATA_EXT}}"] = dateText,
            ["{{DATA_LOCAL}}"] = $"Quartel em {settings.RequestCity}, {dateText}."
        };
        foreach (var row in rows)
        {
            var key = TemplateKey(row.ShortRank);
            mapping[$"{{{{EF_{key}}}}}"] = row.Quantity.ToString(PtBr);
            mapping[$"{{{{UNIT_{key}}}}}"] = row.DailyRateText;
            mapping[$"{{{{SUB_{key}}}}}"] = row.SubtotalText;
            mapping[$"{{{{DIAS_{key}}}}}"] = row.Quantity > 0 ? row.Days.ToString(PtBr) : "0";
        }
        return mapping;
    }

    private static string TemplateKey(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch)).Select(char.ToUpperInvariant).ToArray());
    }

    private static bool TryGenerateFromTemplate(string templatePath, string outputPath, IReadOnlyDictionary<string, string> mapping)
    {
        if (!File.Exists(templatePath)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Copy(templatePath, outputPath, overwrite: true);
            var changed = false;
            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Update))
            {
                foreach (var entry in archive.Entries.Where(x => x.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase) && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    string xml;
                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
                        xml = reader.ReadToEnd();
                    var patched = ReplaceTemplateTokens(xml, mapping)
                        .Replace("w:color w:val=\"FF0000\"", "w:color w:val=\"000000\"", StringComparison.OrdinalIgnoreCase)
                        .Replace("w:color w:val=\"EE0000\"", "w:color w:val=\"000000\"", StringComparison.OrdinalIgnoreCase);
                    if (patched == xml) continue;
                    changed = true;
                    var fullName = entry.FullName;
                    entry.Delete();
                    var replacement = archive.CreateEntry(fullName, CompressionLevel.Optimal);
                    using var writer = new StreamWriter(replacement.Open(), new UTF8Encoding(false));
                    writer.Write(patched);
                }
            }
            if (!changed)
            {
                File.Delete(outputPath);
                return false;
            }
            return true;
        }
        catch
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            return false;
        }
    }

    private static string ReplaceTemplateTokens(string xml, IReadOnlyDictionary<string, string> mapping)
    {
        // Primeira passagem: placeholders que já estão contíguos em um único run.
        foreach (var pair in mapping)
            xml = xml.Replace(pair.Key, SecurityElement.Escape(pair.Value) ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // Segunda passagem: Word costuma dividir {{CHAVE}} em vários runs. Para cada
        // token ainda presente de forma fragmentada, aceita tags XML entre os caracteres.
        foreach (var pair in mapping)
        {
            var tokenPattern = string.Join(@"(?:<[^>]+>)*", pair.Key.Select(ch => System.Text.RegularExpressions.Regex.Escape(ch.ToString())));
            xml = System.Text.RegularExpressions.Regex.Replace(
                xml,
                tokenPattern,
                _ => SecurityElement.Escape(pair.Value) ?? string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
        return xml;
    }

    private static void WriteXlsx(string path, IReadOnlyList<GratificationParticipant> rows, GratificationPeriodInfo period)
    {
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Gratificação 2%" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts>
              <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF0D47A1"/></patternFill></fill></fills>
              <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center"/></xf><xf numFmtId="4" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>
            </styleSheet>
            """);

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var headers = new[] { "NOME COMPLETO", "POSTO", "CPF", "PREC-CP", "SOLDO", "2%/DIA", "INÍCIO", "RETORNO", "DURAÇÃO", "DIAS", "TOTAL" };
        var sheetData = new XElement(ns + "sheetData");
        var header = new XElement(ns + "row", new XAttribute("r", 1));
        for (var i = 0; i < headers.Length; i++) header.Add(InlineCell(ns, ColumnName(i + 1) + "1", headers[i], 1));
        sheetData.Add(header);
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            var r = i + 2;
            var row = new XElement(ns + "row", new XAttribute("r", r));
            row.Add(InlineRichNameCell(ns, "A" + r, item.Military.Name, item.Military.WarName));
            var strings = new[] { MilitaryRankService.ShortName(item.Military.Rank), MilitaryFormatting.FormatCpf(item.Military.Cpf), item.Military.PrecCp };
            for (var c = 0; c < strings.Length; c++) row.Add(InlineCell(ns, ColumnName(c + 2) + r, strings[c], 0));
            row.Add(NumberCell(ns, "E" + r, item.Salary, 2));
            row.Add(NumberCell(ns, "F" + r, item.DailyRate, 2));
            row.Add(InlineCell(ns, "G" + r, FormatDateTimeAbbreviated(period.Start), 0));
            row.Add(InlineCell(ns, "H" + r, FormatDateTimeAbbreviated(period.End), 0));
            row.Add(InlineCell(ns, "I" + r, period.DurationText, 0));
            row.Add(NumberCell(ns, "J" + r, period.IndemnifiableDays, 0));
            row.Add(NumberCell(ns, "K" + r, item.Total, 2));
            sheetData.Add(row);
        }
        var totalRowNumber = rows.Count + 2;
        var totalRow = new XElement(ns + "row", new XAttribute("r", totalRowNumber), InlineCell(ns, "A" + totalRowNumber, "TOTAL GERAL", 1), NumberCell(ns, "K" + totalRowNumber, rows.Sum(x => x.Total), 2));
        sheetData.Add(totalRow);
        var worksheet = new XElement(ns + "worksheet",
            new XElement(ns + "sheetViews", new XElement(ns + "sheetView", new XAttribute("workbookViewId", 0), new XElement(ns + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"), new XAttribute("state", "frozen")))),
            new XElement(ns + "cols", Enumerable.Range(1, headers.Length).Select(i => new XElement(ns + "col", new XAttribute("min", i), new XAttribute("max", i), new XAttribute("width", i == 1 ? 38 : i is 8 or 9 or 10 ? 20 : 15), new XAttribute("customWidth", 1)))),
            sheetData,
            new XElement(ns + "autoFilter", new XAttribute("ref", $"A1:K{Math.Max(1, totalRowNumber)}")));
        WriteEntry(archive, "xl/worksheets/sheet1.xml", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), worksheet).ToString(SaveOptions.DisableFormatting));
    }

    private static XElement InlineCell(XNamespace ns, string reference, string? text, int style)
        => new(ns + "c", new XAttribute("r", reference), new XAttribute("t", "inlineStr"), new XAttribute("s", style), new XElement(ns + "is", new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), SanitizeXml(text))));

    private static XElement InlineRichNameCell(XNamespace ns, string reference, string? fullName, string? warName)
    {
        var inline = new XElement(ns + "is");
        foreach (var segment in NameHighlightHelper.BuildSegments((fullName ?? string.Empty).ToUpper(PtBr), (warName ?? string.Empty).ToUpper(PtBr)))
        {
            var run = new XElement(ns + "r");
            if (segment.IsBold) run.Add(new XElement(ns + "rPr", new XElement(ns + "b")));
            run.Add(new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), SanitizeXml(segment.Text)));
            inline.Add(run);
        }
        return new XElement(ns + "c", new XAttribute("r", reference), new XAttribute("t", "inlineStr"), new XAttribute("s", 0), inline);
    }

    private static XElement NumberCell(XNamespace ns, string reference, decimal value, int style)
        => new(ns + "c", new XAttribute("r", reference), new XAttribute("s", style), new XElement(ns + "v", value.ToString(CultureInfo.InvariantCulture)));

    private static string ColumnName(int index)
    {
        var result = string.Empty;
        while (index > 0) { index--; result = (char)('A' + index % 26) + result; index /= 26; }
        return result;
    }

    private static string SanitizeXml(string? value)
        => new string((value ?? string.Empty).Where(ch => XmlConvert.IsXmlChar(ch)).ToArray());

    private static void WriteDocx(string path, string title, IReadOnlyList<string> paragraphs, IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, string[]? footer = null)
    {
        static string Esc(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
        static string Run(string? text, bool bold = false, int size = 22) => $"<w:r><w:rPr>{(bold ? "<w:b/>" : string.Empty)}<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/><w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\"/></w:rPr><w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r>";
        static string ParagraphXml(string runs, string align = "both") => $"<w:p><w:pPr><w:jc w:val=\"{align}\"/><w:spacing w:after=\"100\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr>{runs}</w:p>";
        static string Cell(string text, bool header = false) => $"<w:tc><w:tcPr><w:tcW w:w=\"1800\" w:type=\"dxa\"/><w:shd w:fill=\"{(header ? "0D47A1" : "FFFFFF")}\"/></w:tcPr><w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:rPr>{(header ? "<w:b/><w:color w:val=\"FFFFFF\"/>" : string.Empty)}<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\"/><w:sz w:val=\"20\"/></w:rPr><w:t xml:space=\"preserve\">{Esc(text)}</w:t></w:r></w:p></w:tc>";

        var body = new StringBuilder();
        body.Append(ParagraphXml(Run(title, true, 30), "center"));
        foreach (var paragraph in paragraphs.Skip(1)) body.Append(ParagraphXml(Run(paragraph)));
        body.Append("<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:color=\"9CA3AF\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"9CA3AF\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"9CA3AF\"/><w:right w:val=\"single\" w:sz=\"4\" w:color=\"9CA3AF\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"D1D5DB\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"D1D5DB\"/></w:tblBorders></w:tblPr>");
        body.Append("<w:tr>").Append(string.Concat(headers.Select(x => Cell(x, true)))).Append("</w:tr>");
        foreach (var row in rows) body.Append("<w:tr>").Append(string.Concat(row.Select(x => Cell(x)))).Append("</w:tr>");
        if (footer is not null) body.Append("<w:tr>").Append(string.Concat(footer.Select(x => Cell(x, true)))).Append("</w:tr>");
        body.Append("</w:tbl>");
        body.Append(ParagraphXml(Run(string.Empty)));
        var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"850\" w:right=\"850\" w:bottom=\"850\" w:left=\"850\"/></w:sectPr></w:body></w:document>";
        var contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>";
        var relationships = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(zip, "[Content_Types].xml", contentTypes);
        WriteEntry(zip, "_rels/.rels", relationships);
        WriteEntry(zip, "word/document.xml", document);
    }

    private static void WriteEntry(ZipArchive archive, string path, string text)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
