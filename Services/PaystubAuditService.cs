using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Auditoria de contracheques integralmente em C#.
/// Localiza o PDF, extrai o texto com PdfPig e interpreta rubricas/rodapé sem Python.
/// </summary>
public sealed partial class PaystubAuditService
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly PaystubService _paystubs;
    private readonly PdfTextService _pdf;
    private readonly LogService _log;

    public PaystubAuditService(PaystubService paystubs, PdfTextService pdf, LogService log)
    {
        _paystubs = paystubs;
        _pdf = pdf;
        _log = log;
    }

    public async Task<PaystubAuditRow> AuditAsync(
        MilitaryRecord military,
        int year,
        int month,
        string? preferredDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var path = await _paystubs.FindBestInDirectoryAsync(military, month, year, preferredDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Empty(military, "Contracheque não salvo para esta competência.");

        try
        {
            var text = await _pdf.ExtractAsync(path, cancellationToken);
            var parsed = Parse(text, military);
            return new PaystubAuditRow
            {
                MilitaryId = military.Id,
                Rank = military.Rank,
                Name = military.Name,
                WarName = military.WarName,
                Cpf = military.Cpf,
                Idt = military.MilitaryId,
                Prec = military.PrecCp,
                Pdf = Path.GetFileName(path),
                PdfPath = path,
                PdfOk = true,
                BankStatus = parsed.BankStatus,
                BankPdf = parsed.BankPdf,
                AgencyPdf = parsed.AgencyPdf,
                AccountPdf = parsed.AccountPdf,
                BankDatabase = military.Bank,
                AgencyDatabase = military.Agency,
                AccountDatabase = military.Account,
                BankDivergent = parsed.BankDivergent,
                PaymentStatus = parsed.PaymentStatus,
                PaymentDifferent = parsed.PaymentDifferent,
                AuxPdf = parsed.AuxPdf,
                AuxDatabase = parsed.AuxDatabase,
                AuxDifference = parsed.AuxDifference,
                AuxStatus = parsed.AuxStatus,
                AuxDr = parsed.AuxDr,
                AuxAr = parsed.AuxAr,
                Fusex = parsed.Fusex,
                DependentFusex = parsed.DependentFusex,
                MedicalFusex = parsed.MedicalFusex,
                FamilySalary = parsed.FamilySalary,
                Dependents = parsed.Dependents,
                PreSchool = parsed.PreSchool,
                Vacation = parsed.Vacation,
                FoodAid = parsed.FoodAid,
                Differences = parsed.Differences,
                Pension = parsed.Pension,
                MilitaryPension = parsed.MilitaryPension,
                Alimony = parsed.Alimony,
                Irrf = parsed.Irrf,
                QualificationAdditional = parsed.QualificationAdditional,
                AvailabilityCompensation = parsed.AvailabilityCompensation,
                Pnr = parsed.Pnr,
                Loans = parsed.Loans,
                Revenue = parsed.Revenue,
                Expense = parsed.Expense,
                Net = parsed.Net,
                Situation = parsed.Situation,
                Lines = parsed.Lines,
                Military = military
            };
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"Falha na auditoria nativa do contracheque de {military.Name}.", ex);
            return new PaystubAuditRow
            {
                MilitaryId = military.Id, Rank = military.Rank, Name = military.Name, WarName = military.WarName,
                Cpf = military.Cpf, Idt = military.MilitaryId, Prec = military.PrecCp,
                Pdf = Path.GetFileName(path), PdfPath = path, PdfOk = true,
                BankDatabase = military.Bank, AgencyDatabase = military.Agency, AccountDatabase = military.Account,
                Situation = "Erro ao ler contracheque: " + ex.Message, Military = military
            };
        }
    }

    private static PaystubAuditRow Empty(MilitaryRecord military, string situation) => new()
    {
        MilitaryId = military.Id, Rank = military.Rank, Name = military.Name, WarName = military.WarName,
        Cpf = military.Cpf, Idt = military.MilitaryId, Prec = military.PrecCp,
        Pdf = "NÃO ENCONTRADO", PdfOk = false,
        BankDatabase = military.Bank, AgencyDatabase = military.Agency, AccountDatabase = military.Account,
        Situation = situation, Military = military
    };

    private static ParsedAudit Parse(string text, MilitaryRecord military)
    {
        var rubrics = ReadRubrics(text);

        var (auxNormal, auxLines) = Sum(rubrics, codes: ["NR0095"], keywords: ["AUX TRANSP", "AUXILIO TRANSP", "TRANSPORTE"]);
        var (auxDr, auxDrLines) = Sum(rubrics, prefixes: ["DR"], keywords: ["AUX TRANSP", "AUXILIO TRANSP", "TRANSPORTE"]);
        var (auxAr, auxArLines) = Sum(rubrics, prefixes: ["AR"], keywords: ["AUX TRANSP", "AUXILIO TRANSP", "TRANSPORTE"]);
        if (auxNormal <= 0 && auxDr <= 0 && auxAr <= 0)
            (auxNormal, auxLines) = Sum(rubrics, keywords: ["AUX TRANSP", "AUXILIO TRANSP", "TRANSPORTE"], excludes: ["BASE", "COMPROVANTE"]);

        var (fusex, fusexLines) = Sum(rubrics, codes: ["ND0001"], keywords: ["FUSEX"]);
        if (fusex <= 0) (fusex, fusexLines) = Sum(rubrics, keywords: ["FUSEX 3%"], excludes: ["DEPENDENTE", "DESPESA MEDICA"]);
        var (depFusex, depFusexLines) = Sum(rubrics, codes: ["ND0011"], keywords: ["FUSEX"]);
        if (depFusex <= 0) (depFusex, depFusexLines) = Sum(rubrics, keywords: ["DESCONTO DEPENDENTE", "DEPENDENTE FUSEX"]);
        var (medFusex, medFusexLines) = Sum(rubrics, codes: ["ND0013"], keywords: ["FUSEX"]);
        if (medFusex <= 0) (medFusex, medFusexLines) = Sum(rubrics, keywords: ["DESPESA MEDICA"]);

        var (family, familyLines) = Sum(rubrics, codes: ["NR0018"], keywords: ["SALARIO FAMILIA"]);
        if (family <= 0) (family, familyLines) = Sum(rubrics, keywords: ["SALARIO FAMILIA", "SAL FAMILIA"]);
        var (preSchool, preSchoolLines) = Sum(rubrics, codes: ["NR0077"], keywords: ["PRE ESCOLAR", "ASSISTENCIA PRE"]);
        if (preSchool <= 0) (preSchool, preSchoolLines) = Sum(rubrics, keywords: ["PRE ESCOLAR", "ASSISTENCIA PRE"]);
        var (vacation, vacationLines) = Sum(rubrics, keywords: ["FERIAS"]);
        var (food, foodLines) = Sum(rubrics, keywords: ["AUX ALIMENT", "AUXILIO ALIMENT", "ALIMENTACAO"]);
        var (drAr, drArLines) = Sum(rubrics, prefixes: ["DR", "AR"]);
        var (arrearsKw, arrearsKwLines) = Sum(rubrics, keywords: ["ATRAS", "EXERCICIO ANTERIOR", "DIFERENCA"]);
        var differences = Math.Max(drAr, arrearsKw);
        var differenceLines = drArLines.Concat(arrearsKwLines).Distinct().Take(10).ToList();
        var (milPension, milPensionLines) = Sum(rubrics, keywords: ["PENSAO MILITAR"]);
        var (alimony, alimonyLines) = Sum(rubrics, keywords: ["PENSAO ALIMENTICIA", "PENSAO ALIMENT"]);
        var (irrf, irrfLines) = Sum(rubrics, keywords: ["IRRF", "IMPOSTO DE RENDA"]);
        var (qualification, qualificationLines) = Sum(rubrics, keywords: ["ADICIONAL DE HABILITACAO", "ADIC HABILIT", "HABILITACAO"]);
        var (availability, availabilityLines) = Sum(rubrics, keywords: ["AD C DISP MIL", "ADICIONAL DE COMPENSACAO"]);
        var (pnr, pnrLines) = Sum(rubrics, keywords: ["PNR", "OCUPACAO PNR"]);
        var (loans, loanLines) = Sum(rubrics, keywords: ["FHE", "FIN IMOB", "EMPREST", "SABEMI", "SEGURO", "CSSE", "PEC"]);

        var revenue = FindFooterMoney(text, "RECEITA");
        var expense = FindFooterMoney(text, "DESPESA");
        var net = FindFooterMoney(text, "LIQUIDO");
        var bankPdf = ExtractLabel(text, "BANCO");
        var agencyPdf = ExtractLabel(text, "AGENCIA");
        var accountPdf = ExtractLabel(text, "C/C");
        if (string.IsNullOrWhiteSpace(accountPdf)) accountPdf = ExtractLabel(text, "CONTA");
        var paymentStatus = ExtractLabel(text, "SITUACAO");
        if (string.IsNullOrWhiteSpace(paymentStatus)) paymentStatus = "—";

        var bankDivergent = IsDifferentBank(bankPdf, military.Bank)
                            || IsDifferentNumber(agencyPdf, military.Agency)
                            || IsDifferentNumber(accountPdf, military.Account);
        var bankStatus = bankDivergent ? "DIVERGENTE" : "OK";
        if (string.IsNullOrWhiteSpace(bankPdf) && string.IsNullOrWhiteSpace(agencyPdf) && string.IsNullOrWhiteSpace(accountPdf))
            bankStatus = "NÃO IDENTIFICADO NO PDF";
        var paymentDifferent = !string.IsNullOrWhiteSpace(paymentStatus)
                               && paymentStatus != "—"
                               && !Normalize(paymentStatus).Contains("NORMAL", StringComparison.Ordinal);

        var auxDb = ParseMoney(military.TransportAidValue);
        double? expectedAux = MilitaryRecord.IsYes(military.ReceivesTransportAid) || auxDb > 0 ? auxDb : null;
        double? auxDiff = expectedAux.HasValue ? auxNormal - expectedAux.Value : null;
        var auxStatus = expectedAux.HasValue
            ? auxNormal > 0 ? Math.Abs(auxDiff!.Value) <= 1.0 ? "OK" : "DIVERGENTE" : "NÃO RECEBEU"
            : auxNormal > 0 ? "RECEBE NO CONTRACHEQUE" : auxDr > 0 ? "SÓ DESCONTO DR" : "—";
        if (auxDr > 0) auxStatus += " + DESC DR";

        var dependents = family > 0 ? Math.Max(1, (int)Math.Round(family / 0.16d)) : 0;
        var alerts = new List<string>();
        if (auxStatus.Contains("DIVERG", StringComparison.OrdinalIgnoreCase)) alerts.Add("Auxílio-transporte diferente");
        if (auxStatus.Contains("NÃO RECEBEU", StringComparison.OrdinalIgnoreCase)) alerts.Add("Auxílio-transporte previsto, mas não localizado");
        if (auxDr > 0) alerts.Add("Desconto de auxílio-transporte (DR)");
        if (auxAr > 0) alerts.Add("Auxílio-transporte AR");
        if (family > 0) alerts.Add($"Salário-família ({dependents} dep.)");
        if (preSchool > 0) alerts.Add("Assistência pré-escolar");
        if (vacation > 0) alerts.Add("Recebeu férias");
        if (food > 0) alerts.Add("Recebeu auxílio-alimentação");
        if (differences > 0) alerts.Add("DR/AR/atrasados/diferença");
        if (fusex <= 0) alerts.Add("Sem FUSEx 3% localizado");
        if (alimony > 0) alerts.Add("Pensão alimentícia");
        if (pnr > 0) alerts.Add("PNR");
        if (loans > 0) alerts.Add("Empréstimo/seguro/FHE");
        if (bankDivergent) alerts.Add("Banco/agência/conta diferente do cadastro");
        if (paymentDifferent) alerts.Add("Situação de pagamento: " + paymentStatus);

        return new ParsedAudit
        {
            BankPdf = bankPdf, AgencyPdf = agencyPdf, AccountPdf = accountPdf,
            BankDivergent = bankDivergent, BankStatus = bankStatus,
            PaymentStatus = paymentStatus, PaymentDifferent = paymentDifferent,
            AuxPdf = auxNormal, AuxDatabase = expectedAux, AuxDifference = auxDiff, AuxStatus = auxStatus,
            AuxDr = auxDr, AuxAr = auxAr, Fusex = fusex, DependentFusex = depFusex, MedicalFusex = medFusex,
            FamilySalary = family, Dependents = dependents, PreSchool = preSchool, Vacation = vacation,
            FoodAid = food, Differences = differences, MilitaryPension = milPension, Alimony = alimony,
            Pension = milPension + alimony, Irrf = irrf, QualificationAdditional = qualification,
            AvailabilityCompensation = availability, Pnr = pnr, Loans = loans,
            Revenue = revenue, Expense = expense, Net = net,
            Situation = alerts.Count == 0 ? "Sem achado relevante" : string.Join("; ", alerts),
            Lines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["aux_transporte"] = auxLines.Take(8).ToList(), ["aux_dr"] = auxDrLines.Take(8).ToList(),
                ["aux_ar"] = auxArLines.Take(8).ToList(), ["fusex"] = fusexLines.Take(5).ToList(),
                ["dep_fusex"] = depFusexLines.Take(5).ToList(), ["med_fusex"] = medFusexLines.Take(5).ToList(),
                ["salario_familia"] = familyLines.Take(5).ToList(), ["pre_escolar"] = preSchoolLines.Take(5).ToList(),
                ["ferias"] = vacationLines.Take(5).ToList(), ["aux_alimentacao"] = foodLines.Take(5).ToList(),
                ["atrasados"] = differenceLines, ["pensao_militar"] = milPensionLines.Take(5).ToList(),
                ["pensao_alimenticia"] = alimonyLines.Take(5).ToList(), ["irrf"] = irrfLines.Take(5).ToList(),
                ["habilitacao"] = qualificationLines.Take(5).ToList(), ["ad_c_disp_mil"] = availabilityLines.Take(5).ToList(),
                ["pnr"] = pnrLines.Take(5).ToList(), ["emprestimos"] = loanLines.Take(5).ToList()
            }
        };
    }

    private static List<Rubric> ReadRubrics(string text)
    {
        var lines = (text ?? string.Empty).Split('\n').Select(OneLine).Where(x => x.Length > 0).ToList();
        var result = new List<Rubric>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string code, string block)
        {
            block = OneLine(block);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(block)) return;
            var moneyValues = MoneyRegex().Matches(block).Select(x => ParseMoney(x.Value)).Where(x => x > 0).ToList();
            var money = moneyValues.Count == 0 ? 0 : moneyValues[^1];
            var key = code + "|" + Normalize(block)[..Math.Min(120, Normalize(block).Length)];
            if (money <= 0 || !seen.Add(key)) return;
            result.Add(new Rubric(code.ToUpperInvariant(), block, Normalize(block), Math.Abs(money)));
        }

        // 1) Layout por linha, quando o PDF veio bem extraído.
        for (var i = 0; i < lines.Count; i++)
        {
            var normalized = Normalize(lines[i]);
            var match = Regex.Match(normalized, @"\b([A-Z]{1,3}\d{3,5})\b");
            if (!match.Success) continue;
            var parts = new List<string> { lines[i] };
            for (var j = i + 1; j < lines.Count && j <= i + 5; j++)
            {
                var next = Normalize(lines[j]);
                if (Regex.IsMatch(next, @"\b[A-Z]{1,3}\d{3,5}\b") || IsFooter(next)) break;
                parts.Add(lines[j]);
            }
            Add(match.Groups[1].Value, string.Join(" ", parts));
        }

        // 2) Fallback por texto contínuo. Alguns PDFs juntam colunas/linhas e o código não
        // fica no início da linha; este regex captura do código até o próximo código/rodapé.
        var normalizedText = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        foreach (Match match in Regex.Matches(normalizedText,
                     @"\b(?<code>[A-Z]{1,3}\d{3,5})\b\s*(?<body>.{0,240}?)(?=\b[A-Z]{1,3}\d{3,5}\b|\bRECEITA\b|\bDESPESA\b|\bL[IÍ]QUIDO\b|$)",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var code = match.Groups["code"].Value;
            var block = (code + " " + match.Groups["body"].Value).Trim();
            if (MoneyRegex().IsMatch(block)) Add(code, block);
        }

        return result;
    }

    private static bool IsFooter(string value) => new[] { "DATA IMP", "DEP IR", "ISENTO IR", "RECEITA", "DESPESA", "LIQUIDO", "BANCO", "AGENCIA", "C/C", "SITUACAO" }.Any(value.StartsWith);

    private static (double Total, List<string> Lines) Sum(
        IEnumerable<Rubric> rubrics,
        string[]? codes = null,
        string[]? prefixes = null,
        string[]? keywords = null,
        string[]? excludes = null)
    {
        var codeSet = codes?.Select(x => x.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keywordSet = keywords?.Select(Normalize).ToArray() ?? [];
        var excludeSet = excludes?.Select(Normalize).ToArray() ?? [];
        var matched = rubrics.Where(r =>
                (codeSet is null || codeSet.Contains(r.Code))
                && (prefixes is null || prefixes.Any(p => r.Code.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                && (keywordSet.Length == 0 || keywordSet.Any(r.Normalized.Contains))
                && (excludeSet.Length == 0 || !excludeSet.Any(r.Normalized.Contains)))
            .Where(r => r.Value > 0)
            .ToList();
        return (matched.Sum(x => x.Value), matched.Select(x => x.Line).Distinct().ToList());
    }

    private static double FindFooterMoney(string text, string label)
    {
        var normalizedLabel = Normalize(label);
        var lines = (text ?? string.Empty).Split('\n').Select(OneLine).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Normalize(lines[i]).Contains(normalizedLabel, StringComparison.Ordinal)) continue;
            var block = string.Join(" ", lines.Skip(i).Take(3));
            var value = MoneyRegex().Matches(block).Select(x => ParseMoney(x.Value)).FirstOrDefault(x => x > 0);
            if (value > 0) return value;
        }
        return 0;
    }

    private static string ExtractLabel(string text, string label)
    {
        var target = Normalize(label).TrimEnd(':');
        var lines = (text ?? string.Empty).Split('\n').Select(OneLine).Where(x => x.Length > 0).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var normalized = Normalize(lines[i]);
            var index = normalized.IndexOf(target, StringComparison.Ordinal);
            if (index < 0) continue;
            var raw = lines[i];
            var colon = raw.IndexOf(':');
            var value = colon >= 0 ? raw[(colon + 1)..].Trim() : string.Empty;
            if (value.Length == 0 && i + 1 < lines.Count) value = lines[i + 1];
            value = Regex.Split(value, @"\b(?:IDT\s+MARGEM|SITUA[ÇC][AÃ]O|BANCO|AG[ÊE]NCIA|C/C|RECEITA|DESPESA|L[IÍ]QUIDO)\b", RegexOptions.IgnoreCase)[0].Trim();
            return value;
        }
        return string.Empty;
    }

    private static bool IsDifferentBank(string pdf, string database)
    {
        if (string.IsNullOrWhiteSpace(pdf) || string.IsNullOrWhiteSpace(database)) return false;
        var a = BankCode(pdf); var b = BankCode(database);
        return a.Length > 0 && b.Length > 0 && !a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDifferentNumber(string pdf, string database)
    {
        if (string.IsNullOrWhiteSpace(pdf) || string.IsNullOrWhiteSpace(database)) return false;
        var a = Digits(pdf).TrimStart('0'); var b = Digits(database).TrimStart('0');
        return a.Length > 0 && b.Length > 0 && !a.Equals(b, StringComparison.Ordinal);
    }

    private static string BankCode(string value)
    {
        var digits = Regex.Match(value ?? string.Empty, @"(?<!\d)\d{3}(?!\d)").Value;
        if (digits.Length == 3) return digits;
        var normalized = Normalize(value);
        if (normalized.Contains("BANCO DO BRASIL")) return "001";
        if (normalized.Contains("ITAU")) return "341";
        if (normalized.Contains("SANTANDER")) return "033";
        if (normalized.Contains("BRADESCO")) return "237";
        if (normalized.Contains("CAIXA") || normalized.Contains("CEF")) return "104";
        return Digits(value).PadLeft(3, '0');
    }

    private static double ParseMoney(string? value)
    {
        var text = (value ?? string.Empty).Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (text.Length == 0) return 0;
        if (text.Contains(',')) text = text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? Math.Abs(number) : 0;
    }

    private static string Digits(string? value) => Regex.Replace(value ?? string.Empty, @"\D+", string.Empty);
    private static string OneLine(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToUpperInvariant(c));
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"(?<!\d)(?:R\$\s*)?[-+]?\d{1,3}(?:\.\d{3})*,\d{2}|(?<!\d)(?:R\$\s*)?[-+]?\d+,\d{2}(?!\d)", RegexOptions.Compiled)]
    private static partial Regex MoneyRegex();

    private sealed record Rubric(string Code, string Line, string Normalized, double Value);

    private sealed class ParsedAudit
    {
        public string BankPdf { get; init; } = string.Empty;
        public string AgencyPdf { get; init; } = string.Empty;
        public string AccountPdf { get; init; } = string.Empty;
        public bool BankDivergent { get; init; }
        public string BankStatus { get; init; } = string.Empty;
        public string PaymentStatus { get; init; } = string.Empty;
        public bool PaymentDifferent { get; init; }
        public double AuxPdf { get; init; }
        public double? AuxDatabase { get; init; }
        public double? AuxDifference { get; init; }
        public string AuxStatus { get; init; } = string.Empty;
        public double AuxDr { get; init; }
        public double AuxAr { get; init; }
        public double Fusex { get; init; }
        public double DependentFusex { get; init; }
        public double MedicalFusex { get; init; }
        public double FamilySalary { get; init; }
        public int Dependents { get; init; }
        public double PreSchool { get; init; }
        public double Vacation { get; init; }
        public double FoodAid { get; init; }
        public double Differences { get; init; }
        public double Pension { get; init; }
        public double MilitaryPension { get; init; }
        public double Alimony { get; init; }
        public double Irrf { get; init; }
        public double QualificationAdditional { get; init; }
        public double AvailabilityCompensation { get; init; }
        public double Pnr { get; init; }
        public double Loans { get; init; }
        public double Revenue { get; init; }
        public double Expense { get; init; }
        public double Net { get; init; }
        public string Situation { get; init; } = string.Empty;
        public Dictionary<string, List<string>> Lines { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
