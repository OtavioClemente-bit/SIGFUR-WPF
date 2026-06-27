using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static partial class AdjustmentAccountsService
{
    public const decimal IncomeTaxDependentDeduction2026 = 189.59m;
    public const decimal SimplifiedMonthlyDiscount2026 = 607.20m;

    private static readonly HashSet<string> NonTaxableMonthlyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AR0018", "AR0077", "AR0078", "AR0092", "AR0094", "AR0070", "AR0083", "AR0099", "AR0100",
        "NR0018", "NR0077", "NR0095", "NR0056", "NR0085"
    };

    private static readonly HashSet<string> MilitaryPensionCodes = new(StringComparer.OrdinalIgnoreCase) { "AD0039", "AD0002", "ND0039", "ND0002" };
    private static readonly HashSet<string> FusexCodes = new(StringComparer.OrdinalIgnoreCase) { "AD0001", "ND0001" };
    private static readonly HashSet<string> FusexMedicalCodes = new(StringComparer.OrdinalIgnoreCase) { "AD0013", "DD0013", "ND0013" };
    private static readonly HashSet<string> FusexDependentCodes = new(StringComparer.OrdinalIgnoreCase) { "AD0011", "ND0011" };
    private static readonly HashSet<string> AlimonyCodes = new(StringComparer.OrdinalIgnoreCase) { "AD0014", "ND0014" };
    private static readonly HashSet<string> PnrCodes = new(StringComparer.OrdinalIgnoreCase) { "AD0003", "AD0008", "AD0021", "ND0003", "ND0008", "ND0021" };

    public static IReadOnlyList<AdjustmentRubric> CreateDefaultRubrics(AdjustmentAccountsSettings settings)
        =>
        [
            New("AR0001", "SOLDO", "R", "+", "DIA", "FIXO", 0m),
            New("AR0003", "ADICIONAL DE HABILITAÇÃO", "R", "+", "DIA", "PERC_SOLDO", settings.QualificationPercent),
            New("AR0014", "ADICIONAL MILITAR", "R", "+", "DIA", "PERC_SOLDO", settings.MilitaryAdditionalPercent),
            New("AR0170", "AD C DISP MIL", "R", "+", "DIA", "PERC_SOLDO", settings.MilitaryAvailabilityPercent),
            New("AR0004", "ADICIONAL DE PERMANÊNCIA", "R", "+", "DIA", "PERC_SOLDO", settings.PermanencePercent),
            New("AR0077", "ASSISTÊNCIA PRÉ-ESCOLAR", "R", "+", "DIA", "FIXO", settings.PreSchoolValue),
            New("AR0018", "SALÁRIO FAMÍLIA", "R", "+", "DIA", "FIXO", settings.FamilySalaryValue),
            New("AD0001", "FUSEX 3%", "D", "-", "DIA", "PERC_VENC", settings.FusexPercent),
            New("AD0039", "PENSÃO MILITAR - MILITAR TEMPORÁRIO", "D", "-", "DIA", "PERC_VENC", settings.MilitaryPensionPercent),
            New("AD0003", "OCUPAÇÃO PNR", "D", "-", "DIA", "PERC_SOLDO", settings.PnrPercent),
            New("AD0011", "DESCONTO DEPENDENTE - FUSEX", "D", "-", "MES", "FIXO", settings.FusexDependentDiscount),
            New("AD0013", "DESPESA MÉDICA - FUSEX", "D", "-", "MES", "FIXO", settings.FusexMedicalExpense),
            New("AD0014", "PENSÃO ALIMENTÍCIA", "D", "-", "MES", "FIXO", settings.AlimonyValue),
            New("AR0092", "ADICIONAL DE FÉRIAS", "R", "+", "MES", "FORM_FER_PROP", 0m),
            New("AR0094", "INDENIZAÇÃO DE FÉRIAS", "R", "+", "MES", "FORM_FER_NGOZ", 0m),
            New("AR0070", "ADICIONAL NATALINO - AJUSTE CONTAS", "R", "+", "MES", "FORM_NAT_TOTAL", 0m),
            New("AR0083", "ADIANTAMENTO DA 1ª PARCELA DO ADC NATALINO", "R", "+", "MES", "FORM_NAT_1P", 0m, included: false),
            New("DR0083", "DEVOLUÇÃO DA 1ª PARCELA DO ADICIONAL NATALINO", "D", "-", "MES", "FORM_NAT_DESC1", 0m)
        ];

    public static AdjustmentCalculationResult Calculate(AdjustmentAccountsSettings settings, IEnumerable<AdjustmentRubric> sourceRubrics)
    {
        var result = new AdjustmentCalculationResult();
        var annualBase = AnnualBase(settings);
        result.AnnualBase = annualBase;

        var calculated = new List<AdjustmentRubric>();
        foreach (var source in sourceRubrics.Where(x => !x.IsAuto))
        {
            var row = source.Clone();
            ApplyDynamicDefaultValue(row, settings);
            var (full, proportional) = CalculateLine(row, settings, annualBase);
            row.FullValue = full;
            row.ProportionalValue = proportional;
            calculated.Add(row);
        }

        var active = calculated.Where(x => x.IsIncluded).ToList();
        result.FullMonthlyTaxableIncome = Sum(active, x => x.Reference == "R" && x.Sign == "+" && !NonTaxableMonthlyCodes.Contains(x.Code), full: true);
        result.ProportionalTaxableIncome = Sum(active, x => x.Reference == "R" && x.Sign == "+" && !NonTaxableMonthlyCodes.Contains(x.Code), full: false);

        var dependentDeduction = Round(Math.Max(0, settings.IncomeTaxDependents) * IncomeTaxDependentDeduction2026);
        result.FullMonthlyIncomeTax = CalculateMonthlyIncomeTax(
            settings,
            result.FullMonthlyTaxableIncome,
            SumCodes(active, MilitaryPensionCodes, true),
            SumCodes(active, FusexCodes, true),
            settings.DeductFusexMedicalExpense ? SumCodes(active, FusexMedicalCodes, true) : 0m,
            settings.DeductFusexDependent ? SumCodes(active, FusexDependentCodes, true) : 0m,
            settings.DeductAlimony ? SumCodes(active, AlimonyCodes, true) : 0m,
            settings.DeductPnr ? SumCodes(active, PnrCodes, true) : 0m,
            dependentDeduction);
        result.ProportionalIncomeTax = CalculateMonthlyIncomeTax(
            settings,
            result.ProportionalTaxableIncome,
            SumCodes(active, MilitaryPensionCodes, false),
            SumCodes(active, FusexCodes, false),
            settings.DeductFusexMedicalExpense ? SumCodes(active, FusexMedicalCodes, false) : 0m,
            settings.DeductFusexDependent ? SumCodes(active, FusexDependentCodes, false) : 0m,
            settings.DeductAlimony ? SumCodes(active, AlimonyCodes, false) : 0m,
            settings.DeductPnr ? SumCodes(active, PnrCodes, false) : 0m,
            dependentDeduction);

        if (settings.IncludeMonthlyIncomeTax)
            AddAutomaticDiscount(calculated, "AD0010", "IMPOSTO DE RENDA (auto)", result.FullMonthlyIncomeTax, result.ProportionalIncomeTax);

        if (settings.IncludeVacationIncomeTax && settings.VacationAdditionalEntitlement)
        {
            var vacationCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AR0092", "AR0094" };
            AddAutomaticDiscount(
                calculated,
                "AD0015",
                "IR ADICIONAL FÉRIAS (auto)",
                Round(SumCodes(active, vacationCodes, true) * 0.275m),
                Round(SumCodes(active, vacationCodes, false) * 0.275m));
        }

        if (settings.IncludeChristmasIncomeTax && settings.ChristmasEntitlement)
        {
            var christmasCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AR0070", "AR0083", "NR0085" };
            AddAutomaticDiscount(
                calculated,
                "AD0028",
                "IR ADICIONAL NATALINO (auto)",
                Round(SumCodes(active, christmasCodes, true) * 0.275m),
                Round(SumCodes(active, christmasCodes, false) * 0.275m));
        }

        foreach (var row in calculated)
        {
            result.Rows.Add(row);
            if (!row.IsIncluded) continue;
            if (row.Reference == "D" || row.Sign == "-") result.Discounts += row.ProportionalValue;
            else result.Earnings += row.ProportionalValue;
        }
        result.Earnings = Round(result.Earnings);
        result.Discounts = Round(result.Discounts);
        return result;
    }

    public static decimal AnnualBase(AdjustmentAccountsSettings settings)
    {
        var salary = Math.Max(0m, settings.Salary);
        var additions = salary * (
            Math.Max(0m, settings.QualificationPercent)
            + Math.Max(0m, settings.MilitaryAdditionalPercent)
            + Math.Max(0m, settings.MilitaryAvailabilityPercent)
            + Math.Max(0m, settings.PermanencePercent)) / 100m;
        return Round(salary + additions);
    }

    public static decimal IncomeTaxTable2026(decimal taxableBase)
    {
        var value = Round(Math.Max(0m, taxableBase));
        if (value <= 2428.80m) return 0m;
        if (value <= 2826.65m) return Round(value * 0.075m - 182.16m);
        if (value <= 3751.05m) return Round(value * 0.15m - 394.16m);
        if (value <= 4664.68m) return Round(value * 0.225m - 675.49m);
        return Round(value * 0.275m - 908.73m);
    }

    public static decimal IncomeTaxReducer2026(decimal taxableIncome, decimal calculatedTax)
    {
        var income = Round(Math.Max(0m, taxableIncome));
        var tax = Round(Math.Max(0m, calculatedTax));
        if (tax <= 0m) return 0m;
        if (income <= 5000m) return Math.Min(tax, 312.89m);
        if (income <= 7350m)
        {
            var reduction = Math.Max(0m, 978.62m - 0.133145m * income);
            return Math.Min(tax, Round(reduction));
        }
        return 0m;
    }

    public static string FormatMoney(decimal value)
        => string.Format(CultureInfo.GetCultureInfo("pt-BR"), "{0:C2}", Round(value));

    public static string FormatCpf(string? value)
    {
        var digits = Digits(value);
        if (digits.Length < 11) return digits;
        digits = digits[..11];
        return $"{digits[..3]}.{digits[3..6]}.{digits[6..9]}-{digits[9..]}";
    }

    public static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    public static IReadOnlyList<AdjustmentBizuRule> DefaultBizuRules()
        =>
        [
            Rule("MODO DE TESTE — Liberar todos os direitos", true, true, true, true,
                "Uso exclusivo para teste e conferência do cálculo.",
                "Libera férias, indenização, adicional natalino e pecuniária para permitir testar todas as rubricas."),
            Rule("Licenciamento Ex-Officio — Término de prorrogação Tp Sv", true, true, true, true,
                "§ 1º do art. 80 do Decreto nº 4.307/2022; Lei nº 7.963/1989.",
                "Regra mais comum para término de prorrogação. Confira meses de férias e de 13º separadamente. No 1º ano, o natalino usa a data de praça; a partir do 2º ano, o ano civil."),
            Rule("Licenciamento Ex-Officio — Conveniência do serviço", true, true, true, false,
                "§ 1º do art. 80 do Decreto nº 4.307/2022; DIEx nº 460-ASSEJUR/SSEF/SEF, de 2 OUT 23.",
                "Faz jus às verbas de férias e natalino, mas o quadro indica que não faz jus à pecuniária."),
            Rule("Licenciamento a pedido / Anulação / Desincorporação", false, false, false, false,
                "Quadro de efeitos pecuniários — ausência de amparo no § 1º do art. 80 do Decreto nº 4.307/2022.",
                "Em regra, não lançar adicional de férias, indenização de férias, 13º nem pecuniária. Verifique eventual situação especial publicada no BI."),
            Rule("Deserção", false, false, false, false,
                "Quadro de efeitos pecuniários; observações sobre adicional natalino no ano corrente.",
                "Não lançar férias, indenização ou natalino como direito. Confira devolução do natalino recebido no ano corrente e se houve ao menos 11 dias trabalhados no mês."),
            Rule("Falecimento", true, true, false, false,
                "Art. 9º da MP nº 2.215/2001; DIEx nº 159-Asse1/SSEF/SEF, de 2 JUN 16.",
                "Férias e indenização são proporcionais aos meses no Exército. O quadro indica que o adicional natalino não é devido."),
            Rule("Licenciamento a Bem da Disciplina (EP)", true, true, true, false,
                "§ 1º do art. 80 do Decreto nº 4.307/2022; DIEx nº 321-S1/4º CGCFEx, de 25 JUN 24.",
                "Lançar férias, indenização e adicional natalino conforme os meses de referência. Pecuniária não é tratada como direito automático."),
            Rule("Exclusão a Bem da Disciplina (EV)", false, false, false, false,
                "Quadro de efeitos pecuniários — ausência de amparo no § 1º do art. 80 do Decreto nº 4.307/2022.",
                "Não lançar férias, indenização, natalino ou pecuniária sem fundamento específico publicado."),
            Rule("Revogação", true, true, true, false,
                "§ 1º do art. 80 do Decreto nº 4.307/2022; DIEx nº 460/2023 e DIEx nº 794-ASSE1/SSEF/SEF, de 23 NOV 22.",
                "Se o militar cumprir o tempo de serviço a que se obrigou, fará jus às verbas principais. Pecuniária consta como não devida."),
            Rule("Aprovação em concurso — Militar de carreira", true, true, true, null,
                "Port GM-MD nº 2.857, de 5 JUN 24; § 1º do art. 80 do Decreto nº 4.307/2022.",
                "Faz jus às verbas principais. Pecuniária depende da coincidência da posse/incorporação com o término da prorrogação."),
            Rule("Aprovação em concurso — Militar temporário", true, true, true, null,
                "Port GM-MD nº 2.857, de 5 JUN 24; § 1º do art. 80 do Decreto nº 4.307/2022.",
                "Faz jus às verbas principais. Confira a pecuniária conforme a data de posse/incorporação e a duração do curso de formação."),
            Rule("Militar gestante acima de 8º ano", true, true, true, true,
                "DIEx nº 151-ASSEJUR/SSEF/SEF, de 15 FEV 24.",
                "O quadro indica as verbas principais e 9 pecuniárias. Confira a publicação e os meses de referência."),
            Rule("Sv Mil Inicial — arrimo/nascimento de filho", null, null, null, null,
                "NT 026-ASSE1/SSEF/SEF, de 10 AGO 21.",
                "Caso especial: conferir Auxílio-Natalidade e Pré-Escolar. Não aplicar automaticamente férias ou 13º por este bizu.")
        ];

    public static void ApplyBizu(AdjustmentAccountsSettings settings, AdjustmentBizuRule rule)
    {
        settings.SelectedBizuTitle = rule.Title;
        if (rule.VacationAdditional.HasValue) settings.VacationAdditionalEntitlement = rule.VacationAdditional.Value;
        if (rule.VacationIndemnity.HasValue) settings.VacationIndemnityEntitlement = rule.VacationIndemnity.Value;
        if (rule.ChristmasAdditional.HasValue) settings.ChristmasEntitlement = rule.ChristmasAdditional.Value;
        if (rule.Pecuniary.HasValue) settings.PecuniaryEntitlement = rule.Pecuniary.Value;
    }

    public static string BulletinIntroductionForReason(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        var normalized = MilitaryRankService.Normalize(text).ToUpperInvariant();
        string Base(string publicationReason)
            => $"Seja realizado o ajuste de contas do militar abaixo relacionado, por motivo de {publicationReason}, sendo excluído e desligado do estado efetivo da {{OM}}, a contar de {{CORTE}}, conforme publicado no BI Nr {{BI}}, de {{BI_DATA}}, da {{OM}}. O cálculo observa as rubricas obrigatórias do SIPPES, incluindo soldo, adicionais, férias, adicional natalino proporcional, descontos legais e demais verbas efetivamente devidas ao caso concreto.";

        if (normalized.Contains("ANUL") && normalized.Contains("INCORPOR")) return Base("anulação de incorporação");
        if (normalized.Contains("EXCL") && normalized.Contains("INCORPOR")) return Base("exclusão de incorporação");
        if (normalized.Contains("DESINCORPOR")) return Base("desincorporação");
        if (normalized.Contains("CONVENI")) return Base("licenciamento ex officio por conveniência do serviço");
        if (normalized.Contains("TERMINO") || normalized.Contains("PRORROG") || normalized.Contains("CONCL") || normalized.Contains("TEMPO")) return Base("licenciamento ex officio por término/conclusão do tempo de serviço");
        if (normalized.Contains("PEDIDO")) return Base("licenciamento a pedido");
        if (normalized.Contains("BEM DA DISCIPLINA") && normalized.Contains("EXCL")) return Base("exclusão a bem da disciplina");
        if (normalized.Contains("BEM DA DISCIPLINA")) return Base("licenciamento a bem da disciplina");
        if (normalized.Contains("DESER")) return Base("deserção");
        if (normalized.Contains("FALEC") || normalized.Contains("OBITO")) return Base("falecimento");
        if (normalized.Contains("REVOG")) return Base("revogação de ato administrativo");
        if (normalized.Contains("OUTRO"))
            return "Seja realizado o ajuste de contas do militar abaixo relacionado, pelo motivo informado nesta abertura, a contar de {CORTE}, conforme publicado no BI Nr {BI}, de {BI_DATA}, da {OM}. O cálculo observa as rubricas obrigatórias do SIPPES, incluindo soldo, adicionais, férias, adicional natalino proporcional, descontos legais e demais verbas efetivamente devidas ao caso concreto.";
        return Base("licenciamento ex officio do serviço ativo do Exército");
    }

    public static string ProfessionalDescription(string description, string code, bool simplify)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AR0001"] = "SOLDO", ["AR0003"] = "ADICIONAL DE HABILITAÇÃO", ["AR0004"] = "ADICIONAL DE PERMANÊNCIA",
            ["AR0014"] = "ADICIONAL MILITAR", ["AR0018"] = "SALÁRIO FAMÍLIA", ["AR0077"] = "ASSISTÊNCIA PRÉ-ESCOLAR",
            ["AR0170"] = "AD C DISP MIL", ["AD0001"] = "FUSEX 3%", ["AD0003"] = "OCUPAÇÃO PNR",
            ["AD0011"] = "DESCONTO DEPENDENTE - FUSEX", ["AD0013"] = "DESPESA MÉDICA - FUSEX", ["AD0014"] = "PENSÃO ALIMENTÍCIA",
            ["AD0039"] = "PENSÃO MILITAR - MILITAR TEMPORÁRIO", ["AR0092"] = "ADICIONAL DE FÉRIAS", ["AR0094"] = "INDENIZAÇÃO DE FÉRIAS",
            ["AR0070"] = "ADICIONAL NATALINO - AJUSTE CONTAS", ["AR0083"] = "ADIANTAMENTO DA 1ª PARCELA DO ADICIONAL NATALINO",
            ["DR0083"] = "DEVOLUÇÃO DA 1ª PARCELA DO ADICIONAL NATALINO", ["AD0010"] = "IMPOSTO DE RENDA",
            ["AD0015"] = "IR ADICIONAL FÉRIAS", ["AD0028"] = "IR ADICIONAL NATALINO"
        };
        var value = map.TryGetValue(code ?? string.Empty, out var official) ? official : (description ?? string.Empty).Trim().ToUpperInvariant();
        if (!simplify) return value;
        value = ParentheticalFormula().Replace(value, match =>
        {
            var inner = match.Groups[1].Value.Trim().ToUpperInvariant();
            return inner is "13º" or "13°" or "13O" ? $" ({inner})" : string.Empty;
        });
        value = InternalFormulaSuffix().Replace(value, string.Empty);
        return MultipleSpaces().Replace(value, " ").Trim();
    }

    public static string FormatBulletinDate(string? value)
    {
        var formats = new[] { "dd/MM/yyyy", "yyyy-MM-dd" };
        if (!DateTime.TryParseExact(value?.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return value?.Trim() ?? string.Empty;
        var months = new[] { "", "JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ" };
        return $"{date:dd} {months[date.Month]} {date:yy}";
    }

    public static string FormatBulletinMilitaryName(MilitaryRecord military)
    {
        var rank = MilitaryRankService.ShortName(military.Rank);
        var name = RemoveRankPrefix(military.Name, military.Rank);
        name = RemoveRankPrefix(name, rank);
        return string.Join(" ", new[] { rank, name }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    public static string ReplaceBulletinTokens(string template, AdjustmentAccountsSettings settings, MilitaryRecord military)
    {
        var name = FormatBulletinMilitaryName(military);
        return (template ?? string.Empty)
            .Replace("{OM}", settings.Organization ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{CORTE}", FormatBulletinDate(settings.CutoffDate), StringComparison.OrdinalIgnoreCase)
            .Replace("{BI}", settings.BulletinNumber ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{BI_DATA}", FormatBulletinDate(settings.BulletinDate), StringComparison.OrdinalIgnoreCase)
            .Replace("{NOME}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{PREC}", Digits(military.PrecCp), StringComparison.OrdinalIgnoreCase)
            .Replace("{CPF}", FormatCpf(military.Cpf), StringComparison.OrdinalIgnoreCase)
            .Replace("{MOTIVO}", settings.BulletinReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static AdjustmentRubric New(string code, string description, string reference, string sign, string @base, string kind, decimal value, bool included = true)
        => new() { Id = code, Code = code, Description = description, Reference = reference, Sign = sign, Base = @base, Kind = kind, Value = value, IsIncluded = included };

    private static AdjustmentBizuRule Rule(string title, bool? vacationAdditional, bool? vacationIndemnity, bool? christmas, bool? pecuniary, string basis, string observation)
        => new() { Title = title, VacationAdditional = vacationAdditional, VacationIndemnity = vacationIndemnity, ChristmasAdditional = christmas, Pecuniary = pecuniary, LegalBasis = basis, Observation = observation };

    private static void ApplyDynamicDefaultValue(AdjustmentRubric row, AdjustmentAccountsSettings settings)
    {
        // Rubricas-base não editadas acompanham os campos laterais. Ao editar uma
        // rubrica-base ela vira override, exatamente como no código Python.
        if (row.IsCustom) return;
        row.Value = row.Code.ToUpperInvariant() switch
        {
            "AR0003" => settings.QualificationPercent,
            "AR0014" => settings.MilitaryAdditionalPercent,
            "AR0170" => settings.MilitaryAvailabilityPercent,
            "AR0004" => settings.PermanencePercent,
            "AR0077" => settings.PreSchoolValue,
            "AR0018" => settings.FamilySalaryValue,
            "AD0001" => settings.FusexPercent,
            "AD0039" => settings.MilitaryPensionPercent,
            "AD0003" => settings.PnrPercent,
            "AD0011" => settings.FusexDependentDiscount,
            "AD0013" => settings.FusexMedicalExpense,
            "AD0014" => settings.AlimonyValue,
            _ => row.Value
        };
    }

    private static (decimal Full, decimal Proportional) CalculateLine(AdjustmentRubric row, AdjustmentAccountsSettings settings, decimal annualBase)
    {
        var full = row.Code.Equals("AR0001", StringComparison.OrdinalIgnoreCase) ? settings.Salary : row.Kind switch
        {
            "PERC_VENC" => Round(annualBase * row.Value / 100m),
            "PERC_SOLDO" => Round(settings.Salary * row.Value / 100m),
            "FIXO" => Round(row.Value),
            "FORM_FER_PROP" => Round(annualBase / 3m),
            "FORM_FER_NGOZ" => Round(annualBase),
            "FORM_NAT_TOTAL" => Round(settings.ReceivedChristmasFirstInstallment ? annualBase / 2m : annualBase),
            "FORM_NAT_1P" => Round(annualBase / 2m),
            "FORM_NAT_DESC1" => settings.ReceivedChristmasFirstInstallment ? Round(annualBase / 2m) : 0m,
            _ => 0m
        };

        decimal proportional;
        if (row.Base == "DIA" || row.Kind == "FIXO")
            proportional = ProportionalByDay(full, settings.DaysInMonth, settings.ServedDays);
        else if (row.Kind is "FORM_FER_PROP" or "FORM_FER_NGOZ")
            proportional = ProportionalByMonth(full, settings.VacationMonths);
        else if (row.Kind is "FORM_NAT_TOTAL" or "FORM_NAT_1P" or "FORM_NAT_DESC1")
            proportional = ProportionalByMonth(full, settings.ChristmasMonths);
        else
            proportional = ProportionalByMonth(full, Math.Max(settings.VacationMonths, settings.ChristmasMonths));

        if (row.Kind == "FORM_FER_PROP" && !settings.VacationAdditionalEntitlement) proportional = 0m;
        if (row.Kind == "FORM_FER_NGOZ" && !settings.VacationIndemnityEntitlement) proportional = 0m;
        if ((row.Kind is "FORM_NAT_TOTAL" or "FORM_NAT_1P" or "FORM_NAT_DESC1") && !settings.ChristmasEntitlement) proportional = 0m;
        return (Round(full), Round(proportional));
    }

    private static decimal CalculateMonthlyIncomeTax(
        AdjustmentAccountsSettings settings,
        decimal taxableIncome,
        decimal militaryPension,
        decimal fusex,
        decimal medical,
        decimal dependentFusex,
        decimal alimony,
        decimal pnr,
        decimal dependents)
    {
        var taxableBase = Round(Math.Max(0m, taxableIncome - militaryPension - fusex - medical - dependentFusex - alimony - pnr - dependents));
        var tax = IncomeTaxTable2026(taxableBase);
        if (settings.ApplyIncomeTaxReducer2026) tax = Math.Max(0m, Round(tax - IncomeTaxReducer2026(taxableIncome, tax)));
        return Round(tax);
    }

    private static void AddAutomaticDiscount(List<AdjustmentRubric> rows, string code, string description, decimal full, decimal proportional)
    {
        full = Round(full);
        proportional = Round(proportional);
        if (full <= 0m && proportional <= 0m) return;
        rows.Add(new AdjustmentRubric
        {
            Id = "AUTO_" + code,
            Code = code,
            Description = description,
            Reference = "D",
            Sign = "-",
            Base = "MES",
            Kind = "AUTO",
            Value = full,
            IsIncluded = true,
            IsAuto = true,
            FullValue = full,
            ProportionalValue = proportional
        });
    }

    private static decimal Sum(IEnumerable<AdjustmentRubric> rows, Func<AdjustmentRubric, bool> predicate, bool full)
        => Round(rows.Where(predicate).Sum(x => full ? x.FullValue : x.ProportionalValue));

    private static decimal SumCodes(IEnumerable<AdjustmentRubric> rows, HashSet<string> codes, bool full)
        => Round(rows.Where(x => codes.Contains(x.Code)).Sum(x => full ? x.FullValue : x.ProportionalValue));

    private static decimal ProportionalByDay(decimal full, int daysInMonth, int servedDays)
        => daysInMonth <= 0 ? 0m : Round(full * Math.Max(0, servedDays) / daysInMonth);

    private static decimal ProportionalByMonth(decimal full, int months)
        => months <= 0 ? 0m : Round(full * Math.Clamp(months, 0, 12) / 12m);

    private static bool IsFormula(string kind) => kind.StartsWith("FORM_", StringComparison.OrdinalIgnoreCase);
    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string RemoveRankPrefix(string name, string rank)
    {
        var current = (name ?? string.Empty).Trim();
        var candidate = (rank ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(candidate)) return current;
        return current.StartsWith(candidate + " ", StringComparison.CurrentCultureIgnoreCase) ? current[candidate.Length..].Trim() : current;
    }

    [GeneratedRegex(@"\s*\(([^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ParentheticalFormula();

    [GeneratedRegex(@"\s+—\s*(?:TOTAL|1ª\s*PARCELA|1A\s*PARCELA).*$", RegexOptions.IgnoreCase)]
    private static partial Regex InternalFormulaSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpaces();
}
