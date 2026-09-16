using System.Globalization;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class AdjustmentNumericParser
{
    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0m;
        var cleaned = (text ?? string.Empty).Trim().Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"[^0-9,\.\-+]", string.Empty, RegexOptions.CultureInvariant);
        if (string.IsNullOrWhiteSpace(cleaned)) return false;

        var comma = cleaned.LastIndexOf(',');
        var dot = cleaned.LastIndexOf('.');
        string normalized;
        if (comma >= 0 && dot >= 0)
        {
            // O último separador é o decimal; os anteriores são agrupadores.
            normalized = comma > dot
                ? cleaned.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')
                : cleaned.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else if (comma >= 0)
        {
            normalized = cleaned.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        }
        else if (dot >= 0)
        {
            var dotCount = cleaned.Count(ch => ch == '.');
            var fractionalDigits = cleaned.Length - dot - 1;
            // Um único ponto com 1 ou 2 casas é decimal gravado em formato invariável
            // (ex.: 1234.56). Pontos repetidos ou três casas são milhar brasileiro.
            normalized = dotCount == 1 && fractionalDigits is 1 or 2
                ? cleaned
                : cleaned.Replace(".", string.Empty, StringComparison.Ordinal);
        }
        else
        {
            normalized = cleaned;
        }

        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);
    }
}

public static class AdjustmentQuotaCalculator
{
    public const int MinimumDaysForQuota = 15;

    public static IReadOnlyList<AdjustmentQuotaDetail> Calculate(DateTime start, DateTime end)
    {
        if (end.Date < start.Date) return [];
        var result = new List<AdjustmentQuotaDetail>();
        var cursor = new DateTime(start.Year, start.Month, 1);
        var finalMonth = new DateTime(end.Year, end.Month, 1);

        while (cursor <= finalMonth)
        {
            var monthEnd = cursor.AddMonths(1).AddDays(-1);
            var overlapStart = start.Date > cursor ? start.Date : cursor;
            var overlapEnd = end.Date < monthEnd ? end.Date : monthEnd;
            var computableDays = overlapEnd < overlapStart ? 0 : (overlapEnd - overlapStart).Days + 1;
            var fullMonth = overlapStart == cursor && overlapEnd == monthEnd;
            var counts = computableDays >= MinimumDaysForQuota;
            var explanation = fullMonth
                ? "Mês integral dentro do período: 1 cota."
                : counts
                    ? computableDays == MinimumDaysForQuota
                        ? $"Fração com exatamente {MinimumDaysForQuota} dias: 1 cota pela regra mínima."
                        : $"Fração com {computableDays} dias: 1 cota por atingir o mínimo de {MinimumDaysForQuota} dias."
                    : $"Fração com {computableDays} dias: não gera cota por ser inferior a {MinimumDaysForQuota} dias.";

            result.Add(new AdjustmentQuotaDetail(
                cursor.Year, cursor.Month, computableDays, DateTime.DaysInMonth(cursor.Year, cursor.Month),
                fullMonth, counts, explanation));
            cursor = cursor.AddMonths(1);
        }

        return result;
    }
}

public sealed record EligibilityDecision(bool IsEligible, string Reason, bool RequiresHumanConfirmation = false);

public static class AdjustmentRulesEngine
{
    public const string RulesVersion = "AJUSTE-CONTAS-4.1.0";

    public static DateTime? ResolveEffectiveEntitlementStart(AdjustmentDraft draft)
        => draft.EntitlementStart?.Date
           ?? (draft.MonthlyAdjustmentOnly && draft.EntitlementEnd is { } end
               ? new DateTime(end.Year, end.Month, 1)
               : null);

    public static DateTime? ResolveVacationQuotaStart(AdjustmentDraft draft)
        => draft.VacationQuotaStart?.Date ?? draft.EntitlementStart?.Date;

    public static DateTime ResolveVacationQuotaEnd(AdjustmentDraft draft)
        => draft.VacationQuotaEnd?.Date ?? DateTime.Today;

    public static int ResolveDaysInMonth(AdjustmentDraft draft)
    {
        if (draft.IsTestMode && draft.TestDaysInMonthOverride is { } testDays) return testDays;
        var reference = draft.EntitlementEnd ?? ResolveEffectiveEntitlementStart(draft);
        return reference is { } date ? DateTime.DaysInMonth(date.Year, date.Month) : 0;
    }

    public static int ResolveServedDays(AdjustmentDraft draft)
    {
        if (draft.IsTestMode && draft.TestServedDaysOverride is { } testDays) return testDays;
        return ResolveEffectiveEntitlementStart(draft) is { } start
               && draft.EntitlementEnd is { } end
               && end.Date >= start.Date
            ? (end.Date - start.Date).Days + 1
            : 0;
    }

    public static int ResolveQualificationDays(AdjustmentDraft draft)
    {
        var periodStart = ResolveEffectiveEntitlementStart(draft);
        if (periodStart is null || draft.EntitlementEnd is not { } end) return 0;
        var qualificationStart = draft.QualificationEffectiveDate?.Date ?? periodStart.Value.Date;
        if (qualificationStart < periodStart.Value.Date) qualificationStart = periodStart.Value.Date;
        return qualificationStart > end.Date ? 0 : (end.Date - qualificationStart).Days + 1;
    }

    public static EligibilityDecision CanReceiveQualification(AdjustmentDraft draft, QualificationOption option)
    {
        if (draft.IsTestMode)
            return new EligibilityDecision(true, "Modo de teste: percentual e vigência informados manualmente; boletim bloqueado.");

        var profile = MilitaryRemunerationCatalog.Resolve(draft.HistoricalRank, draft.Situation, ResolveEffectiveEntitlementStart(draft));

        if (option.IsNone)
            return new EligibilityDecision(true, "Nenhum adicional de habilitação foi informado para o período.");

        if (draft.Situation == AdjustmentSituationKind.NotInformed)
            return new EligibilityDecision(false, "A situação militar no período precisa ser informada.", true);

        if (!profile.IsQualificationAllowed(option.Code))
            return new EligibilityDecision(false, profile.Explanation);

        if (profile.QualificationMode == QualificationSelectionMode.FixedFormation)
            return new EligibilityDecision(true, profile.Explanation);

        if (draft.QualificationEffectiveDate is { } qualificationStart
            && draft.EntitlementEnd is { } end
            && qualificationStart.Date > end.Date)
            return new EligibilityDecision(false, "A habilitação iniciou depois do período e não pode retroagir.");

        if (draft.QualificationEffectiveDate is { } partialStart
            && ResolveEffectiveEntitlementStart(draft) is { } start
            && partialStart.Date > start.Date)
            return new EligibilityDecision(true, $"Vigência parcial a partir de {partialStart:dd/MM/yyyy}; o adicional será calculado por {ResolveQualificationDays(draft)} dia(s)." );

        if (draft.Situation == AdjustmentSituationKind.RecruitSpecialExtension)
            return new EligibilityDecision(true, "Situação especial/dilação analisada com comprovação e vigência explícitas; não foi aplicada a vedação do recruta comum.");

        if (draft.Situation is AdjustmentSituationKind.AspirantEipot or AdjustmentSituationKind.AspirantEic or AdjustmentSituationKind.AspirantOtt)
            return new EligibilityDecision(true, "Estágio identificado; direito aceito somente porque a comprovação individual e a vigência foram informadas.");

        return new EligibilityDecision(true, "Habilitação selecionada e vigente durante todo o período informado.");
    }

    public static string ResolveHistoricalRank(AdjustmentDraft draft)
        => MilitaryRankService.Canonicalize(draft.HistoricalRank);

    public static int ResolveVacationQuotas(AdjustmentDraft draft)
        => draft.VacationQuotasOverride is { } explicitQuotas
            ? Math.Clamp(explicitQuotas, 0, 12)
            : ResolveVacationQuotaStart(draft) is { } start
                ? AdjustmentQuotaCalculator.Calculate(start, ResolveVacationQuotaEnd(draft)).Count(x => x.CountsAsQuota)
                : 0;
}

public static class AdjustmentValidationEngine
{
    public static IReadOnlyList<AdjustmentValidationMessage> Validate(
        AdjustmentDraft draft,
        RemunerationReference remuneration,
        IReadOnlyList<AdjustmentQuotaDetail> quotas,
        QualificationOption qualification,
        EligibilityDecision qualificationDecision)
    {
        var messages = new List<AdjustmentValidationMessage>();

        if (!draft.IsManualSimulation && (draft.MilitaryId == 0 || string.IsNullOrWhiteSpace(draft.MilitaryName)))
            Error("MILITARY_REQUIRED", "Selecione o militar do ajuste.", "MilitaryId");
        if (draft.IsManualSimulation)
            Info("MANUAL_SIMULATION", "Simulação sem militar: o cálculo é permitido, mas a geração de boletim permanece bloqueada.");
        if (draft.IsTestMode)
            Warning("TEST_MODE", "MODO DE TESTE ATIVO: soldo, percentuais e dias podem ser alterados. A geração de boletim está bloqueada.");
        if (!draft.IsTestMode && string.IsNullOrWhiteSpace(draft.HistoricalRank))
            Error("HISTORICAL_RANK_REQUIRED", "P/Grad real do período do direito não foi informado.", "HistoricalRank");
        if (!draft.IsTestMode && draft.Situation == AdjustmentSituationKind.NotInformed)
            Error("SITUATION_REQUIRED", "Informe a situação militar existente no período do direito.", "Situation");
        var effectiveStart = AdjustmentRulesEngine.ResolveEffectiveEntitlementStart(draft);
        if (draft.EntitlementStart is null && !draft.MonthlyAdjustmentOnly)
            Error("START_REQUIRED", "Informe a data inicial do período do direito.", "EntitlementStart");
        if (draft.EntitlementStart is null && draft.MonthlyAdjustmentOnly && draft.EntitlementEnd is { } monthlyDate)
            Info("MONTHLY_PERIOD_INFERRED", $"Ajuste somente do mês: início considerado em 01/{monthlyDate:MM/yyyy} e término em {monthlyDate:dd/MM/yyyy}.");
        if (draft.EntitlementEnd is null)
            Error("END_REQUIRED", "Informe a data final do período do direito.", "EntitlementEnd");
        if (effectiveStart is { } start && draft.EntitlementEnd is { } end && end.Date < start.Date)
            Error("INVALID_PERIOD", "Data final anterior à data inicial.", "EntitlementEnd");
        if (effectiveStart is { } monthlyStart && draft.EntitlementEnd is { } monthlyEnd
            && (monthlyStart.Year != monthlyEnd.Year || monthlyStart.Month != monthlyEnd.Month))
            Error("PAY_PERIOD_MUST_BE_ONE_MONTH", "O período de remuneração deve ficar dentro de uma única competência. Para férias, use a data própria de início das cotas.", "EntitlementStart");
        if (!draft.IsTestMode && remuneration.Salary <= 0m && !string.IsNullOrWhiteSpace(draft.HistoricalRank))
            Error("SALARY_NOT_FOUND", "Não foi encontrado soldo para o P/Grad real e a vigência informados.", "HistoricalRank");
        var legalProfile = MilitaryRemunerationCatalog.Resolve(draft.HistoricalRank, draft.Situation, effectiveStart);
        if (!draft.IsTestMode && !string.IsNullOrWhiteSpace(draft.HistoricalRank)
            && legalProfile.MilitaryAdditionalPercent == 0m
            && legalProfile.AvailabilityPercent == 0m)
            Error("LEGAL_PROFILE_NOT_FOUND", "O P/Grad não possui perfil no catálogo legal de adicionais. Corrija o P/Grad antes de calcular.", "HistoricalRank");
        WarnPercentageOverride(draft.MilitaryAdditionalPercentOverridden, draft.MilitaryAdditionalPercent, legalProfile.MilitaryAdditionalPercent, "adicional militar");
        WarnPercentageOverride(draft.AvailabilityPercentOverridden, draft.AvailabilityPercent, legalProfile.AvailabilityPercent, "adicional de disponibilidade");
        WarnPercentageOverride(draft.FusexPercentOverridden, draft.FusexPercent, legalProfile.FusexPercent, "FUSEx");
        WarnPercentageOverride(draft.MilitaryPensionPercentOverridden, draft.MilitaryPensionPercent, legalProfile.MilitaryPensionPercent, "pensão militar");
        if (effectiveStart is { } periodStart && draft.EntitlementEnd is { } periodEnd
            && CrossesRemunerationBoundary(periodStart, periodEnd))
            Error("REMUNERATION_TABLE_BOUNDARY", "O período atravessa duas vigências de soldo. Divida o ajuste por vigência para impedir o uso de uma única tabela em todo o período.", "EntitlementEnd");

        ValidateNonNegative(draft.MilitaryAdditionalPercent, "adicional militar", "MilitaryAdditionalPercent");
        ValidateNonNegative(draft.AvailabilityPercent, "adicional de disponibilidade", "AvailabilityPercent");
        ValidateNonNegative(draft.PermanencePercent, "adicional de permanência", "PermanencePercent");
        ValidateNonNegative(draft.PreSchoolValue, "assistência pré-escolar", "PreSchoolValue");
        ValidateNonNegative(draft.FamilySalaryValue, "salário-família", "FamilySalaryValue");
        if (draft.FamilySalaryDependents is < 0 or > 99)
            Error("INVALID_FAMILY_SALARY_DEPENDENTS", "A quantidade de dependentes do salário-família deve estar entre 0 e 99.", "FamilySalaryDependents");
        if (draft.FamilySalaryValue != FamilySalaryQuotaCatalog.Resolve(draft.FamilySalaryDependents))
            Error("INVALID_MILITARY_FAMILY_SALARY", "O salário-família militar deve corresponder a R$ 0,16 por dependente.", "FamilySalaryValue");
        ValidateNonNegative(draft.FusexPercent, "FUSEx", "FusexPercent");
        ValidateNonNegative(draft.MilitaryPensionPercent, "pensão militar", "MilitaryPensionPercent");
        ValidateNonNegative(draft.PnrPercent, "PNR", "PnrPercent");
        ValidateNonNegative(draft.FusexDependentDiscount, "FUSEx dependente", "FusexDependentDiscount");
        ValidateNonNegative(draft.FusexMedicalExpense, "despesa médica FUSEx", "FusexMedicalExpense");
        ValidateNonNegative(draft.AlimonyValue, "pensão alimentícia", "AlimonyValue");
        if (draft.ChristmasQuotas is < 0 or > 12)
            Error("INVALID_CHRISTMAS_QUOTAS", "Quantidade de cotas de 13º deve estar entre 0 e 12.", "ChristmasQuotas");
        if (draft.VacationQuotasOverride is < 0 or > 12)
            Error("INVALID_VACATION_QUOTAS", "Quantidade manual de cotas de férias deve estar entre 0 e 12.", "VacationQuotasOverride");
        if (draft.PecuniaryQuotas is < 0 or > 50)
            Error("INVALID_PECUNIARY_QUOTAS", "Quantidade de compensações pecuniárias deve estar entre 0 e 50.", "PecuniaryQuotas");
        if (draft.IncomeTaxDependents is < 0 or > 99)
            Error("INVALID_INCOME_TAX_DEPENDENTS", "Quantidade de dependentes para IR deve estar entre 0 e 99.", "IncomeTaxDependents");
        var daysInMonth = AdjustmentRulesEngine.ResolveDaysInMonth(draft);
        var servedDays = AdjustmentRulesEngine.ResolveServedDays(draft);
        if (daysInMonth is < 1 or > 31)
            Error("INVALID_DAYS_IN_MONTH", "Dias do mês deve estar entre 1 e 31.", "TestDaysInMonthOverride");
        if (servedDays < 0 || servedDays > daysInMonth)
            Error("INVALID_SERVED_DAYS", "Dias remunerados deve estar entre zero e a quantidade de dias do mês.", "TestServedDaysOverride");
        if (draft.IsTestMode && draft.TestSalaryOverride is null or < 0m)
            Error("TEST_SALARY_REQUIRED", "No modo de teste, informe um soldo igual ou superior a zero.", "TestSalaryOverride");

        if (!qualification.IsNone && !qualificationDecision.IsEligible)
            Error("QUALIFICATION_INELIGIBLE", $"Adicional de habilitação selecionado, mas não é permitido: {qualificationDecision.Reason}", "QualificationCode");

        if (draft.PecuniaryEntitlement && draft.PecuniaryQuotas <= 0)
            Error("PECUNIARY_QUOTAS_REQUIRED", "Informe a quantidade de remunerações da compensação pecuniária antes de gerar o boletim.", "PecuniaryQuotas");

        var reasonRule = AdjustmentAccountsService.ResolveBizuRule(draft.AdjustmentReason);
        if (!draft.IsTestMode && string.IsNullOrWhiteSpace(draft.AdjustmentReason))
            Error("REASON_REQUIRED", "Selecione o motivo do ajuste.", "AdjustmentReason");
        else if (!draft.IsTestMode && reasonRule is null)
            Error("REASON_NOT_CLASSIFIED", "O motivo não corresponde ao catálogo central. Selecione a modalidade correta ou 'Outros / editar manualmente' para não reutilizar direitos de uma regra antiga.", "AdjustmentReason");
        else if (!draft.IsTestMode && reasonRule is not null)
        {
            ValidateForbiddenRight(reasonRule.VacationAdditional, draft.VacationAdditionalEntitlement, "adicional de férias", "VacationAdditionalEntitlement");
            ValidateForbiddenRight(reasonRule.VacationIndemnity, draft.VacationIndemnityEntitlement, "indenização de férias", "VacationIndemnityEntitlement");
            ValidateForbiddenRight(reasonRule.ChristmasAdditional, draft.ChristmasEntitlement, "adicional natalino", "ChristmasEntitlement");
            ValidateForbiddenRight(reasonRule.Pecuniary, draft.PecuniaryEntitlement, "compensação pecuniária", "PecuniaryEntitlement");
        }

        if (legalProfile.ReceivesMinimumWageComplement && effectiveStart is { } minimumDate)
            Info("MINIMUM_WAGE_COMPLEMENT", $"NR0026 será calculada automaticamente pela diferença para o salário mínimo vigente de {MinimumWageCatalog.Resolve(minimumDate):C2}; não integra a base de adicionais ou descontos.");

        if (!string.IsNullOrWhiteSpace(draft.CurrentRank)
            && !string.IsNullOrWhiteSpace(draft.HistoricalRank)
            && !MilitaryRankService.Canonicalize(draft.CurrentRank).Equals(MilitaryRankService.Canonicalize(draft.HistoricalRank), StringComparison.OrdinalIgnoreCase))
            Warning("HISTORICAL_RANK_USED", $"O P/Grad atual ({draft.CurrentRank}) difere do P/Grad do período ({draft.HistoricalRank}). O cálculo usa o P/Grad histórico.");

        if (qualification.IsNone)
            Info("QUALIFICATION_ZERO", "Adicional de Habilitação: R$ 0,00. Nenhum direito comprovado foi aplicado.");

        foreach (var quota in quotas.Where(x => x.ComputableDays is 14 or 15))
            Info("QUOTA_BOUNDARY", quota.Explanation);

        var quotaCount = AdjustmentRulesEngine.ResolveVacationQuotas(draft);
        if (AdjustmentRulesEngine.ResolveVacationQuotaStart(draft) is { } quotaStart
            && AdjustmentRulesEngine.ResolveVacationQuotaEnd(draft) < quotaStart)
            Error("INVALID_VACATION_QUOTA_PERIOD", "A data final das cotas não pode ser anterior à data inicial.", "VacationQuotaEnd");
        if ((draft.VacationIndemnityEntitlement || draft.VacationAdditionalEntitlement)
            && draft.VacationQuotasOverride is null
            && AdjustmentRulesEngine.ResolveVacationQuotaStart(draft) is not null
            && draft.VacationQuotaEnd is null)
            Info("VACATION_QUOTA_END_INFERRED", $"Fim das cotas não informado: considerada a data atual, {DateTime.Today:dd/MM/yyyy}.");
        if ((draft.VacationIndemnityEntitlement || draft.VacationAdditionalEntitlement)
            && draft.VacationQuotasOverride is null
            && AdjustmentRulesEngine.ResolveVacationQuotaStart(draft) is null)
            Error("VACATION_QUOTA_SOURCE_REQUIRED", "Para pagar férias, informe a quantidade de cotas ou a data de início das cotas (pode usar a data de praça).", "VacationQuotaStart");
        if ((draft.VacationIndemnityEntitlement || draft.VacationAdditionalEntitlement) && quotaCount <= 0)
            Error("VACATION_QUOTAS_REQUIRED", "O direito a férias está marcado, mas a quantidade calculada/informada é zero.", "VacationQuotasOverride");
        if (quotaCount > 12)
            Error("VACATION_QUOTAS_EXCEED_YEAR", "O período das cotas gerou mais de 12 meses. Informe a aquisição correta ou divida o ajuste por exercício.", "VacationQuotaStart");
        if (draft.VacationIndemnityEntitlement)
            Info("AR0094_PARAMETER", $"AR0094 será lançado no SIPPES com parâmetro Quantidade de Meses = {quotaCount}.");
        if (draft.VacationAdditionalEntitlement)
            Info("AR0096_PARAMETER", $"AR0096 será lançado no SIPPES com parâmetro Quantidade de Meses = {quotaCount}.");

        if (!string.IsNullOrWhiteSpace(draft.LegacyMigrationNote))
            Warning("LEGACY_MIGRATION", draft.LegacyMigrationNote);

        return messages;

        static bool CrossesRemunerationBoundary(DateTime start, DateTime end)
        {
            DateTime[] boundaries = [new(2025, 4, 1), new(2026, 1, 1)];
            return boundaries.Any(boundary => start.Date < boundary && end.Date >= boundary);
        }

        void Error(string code, string message, string field = "")
            => messages.Add(new AdjustmentValidationMessage(AdjustmentValidationSeverity.BlockingError, code, message, field));
        void Warning(string code, string message, string field = "")
            => messages.Add(new AdjustmentValidationMessage(AdjustmentValidationSeverity.Warning, code, message, field));
        void Info(string code, string message, string field = "")
            => messages.Add(new AdjustmentValidationMessage(AdjustmentValidationSeverity.Information, code, message, field));
        void ValidateNonNegative(decimal value, string label, string field)
        {
            if (value < 0m) Error("NEGATIVE_VALUE", $"{label} não pode ser negativo.", field);
            if (value > 100m && field.EndsWith("Percent", StringComparison.Ordinal))
                Error("INVALID_PERCENTAGE", $"Percentual de {label} acima de 100% exige correção.", field);
        }
        void ValidateForbiddenRight(bool? allowed, bool selected, string label, string field)
        {
            if (allowed == false && selected)
                Error("RIGHT_FORBIDDEN_BY_REASON", $"O motivo selecionado não prevê {label} no quadro de efeitos. Desmarque o direito ou escolha o motivo correto.", field);
        }
        void WarnPercentageOverride(bool overridden, decimal informed, decimal usual, string label)
        {
            if (overridden && informed != usual)
                Warning("PERCENTAGE_MANUAL_OVERRIDE", $"Percentual excepcional de {label}: informado {informed:0.##}%, enquanto o perfil usual indica {usual:0.##}%. Confira o fundamento antes de gerar.");
        }
    }
}

public static class AdjustmentCalculationEngine
{
    public static AdjustmentSimulationResult Simulate(AdjustmentDraft sourceDraft, RemunerationReference remuneration)
    {
        var draft = sourceDraft.Clone();
        draft.HistoricalRank = AdjustmentRulesEngine.ResolveHistoricalRank(draft);
        if (!draft.IsTestMode) MilitaryRemunerationCatalog.ApplyFixedPercentages(draft);
        draft.UpdatedAtUtc = DateTime.UtcNow;
        var effectiveStart = AdjustmentRulesEngine.ResolveEffectiveEntitlementStart(draft);
        var qualification = QualificationCatalog.Resolve(draft.QualificationCode, effectiveStart);
        if (draft.IsTestMode && draft.TestQualificationPercentOverride is { } testQualificationPercent)
            qualification = qualification with
            {
                Description = "Habilitação manual (teste)",
                Percentage = Math.Max(0m, testQualificationPercent),
                Source = "Override do modo de teste"
            };
        var qualificationDecision = AdjustmentRulesEngine.CanReceiveQualification(draft, qualification);
        var quotas = AdjustmentRulesEngine.ResolveVacationQuotaStart(draft) is { } start
            ? AdjustmentQuotaCalculator.Calculate(start, AdjustmentRulesEngine.ResolveVacationQuotaEnd(draft))
            : [];

        var salary = Round(Math.Max(0m, remuneration.Salary));
        var qualificationValue = qualificationDecision.IsEligible ? Percent(salary, qualification.Percentage) : 0m;
        var militaryAdditional = Percent(salary, draft.MilitaryAdditionalPercent);
        var availability = Percent(salary, draft.AvailabilityPercent);
        var permanence = Percent(salary, draft.PermanencePercent);
        var remunerationBase = Round(salary + qualificationValue + militaryAdditional + availability + permanence);
        var profile = MilitaryRemunerationCatalog.Resolve(draft.HistoricalRank, draft.Situation, effectiveStart);
        var minimumWage = MinimumWageCatalog.Resolve(effectiveStart ?? remuneration.ReferenceDate);
        var minimumWageComplement = profile.ReceivesMinimumWageComplement
            ? Round(Math.Max(0m, minimumWage - remunerationBase))
            : 0m;
        var daysInMonth = AdjustmentRulesEngine.ResolveDaysInMonth(draft);
        var servedDays = AdjustmentRulesEngine.ResolveServedDays(draft);
        var qualificationDays = qualificationDecision.IsEligible && !qualification.IsNone
            ? Math.Min(servedDays, AdjustmentRulesEngine.ResolveQualificationDays(draft))
            : 0;
        var effectiveQualificationPercent = servedDays > 0
            ? Round(qualification.Percentage * qualificationDays / servedDays)
            : 0m;
        var monthlySettings = MonthlySettings(draft, salary, daysInMonth, servedDays, effectiveQualificationPercent);
        var monthlyCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AR0001", "AR0003", "AR0014", "AR0170", "AR0004", "AR0077", "AR0018",
            "AD0001", "AD0039", "AD0003", "AD0011", "AD0013", "AD0014"
        };
        var monthlyRubrics = AdjustmentAccountsService.CreateDefaultRubrics(monthlySettings)
            .Where(x => monthlyCodes.Contains(x.Code))
            .ToList();
        var monthlyResult = AdjustmentAccountsService.Calculate(monthlySettings, monthlyRubrics);
        var components = monthlyResult.Rows
            .Where(x => x.IsIncluded)
            .Select(row => MonthlyComponent(row, draft, qualification, qualificationDecision, salary, qualificationValue,
                servedDays, qualificationDays, daysInMonth))
            .ToList();

        var quotaCount = AdjustmentRulesEngine.ResolveVacationQuotas(draft);
        var quotaFraction = quotaCount / 12m;
        if (profile.ReceivesMinimumWageComplement)
        {
            var proportionalMinimumComplement = Proportional(minimumWageComplement, servedDays, daysInMonth);
            components.Add(new AdjustmentComponentResult(
                "NR0026", "Complemento do salário mínimo", "Complemento legal automático", true,
                $"Diferença entre o salário mínimo de {minimumWage:C2} e a remuneração consolidada elegível.",
                minimumWage, 0m, servedDays, minimumWageComplement, proportionalMinimumComplement,
                MinimumWageCatalog.LegalBasis, true));
        }

        if (draft.VacationIndemnityEntitlement)
            components.Add(new AdjustmentComponentResult(
                "AR0094", "Indenização de férias", "Direito proporcional", true,
                "Direito explicitamente confirmado no ajuste.", remunerationBase, 0m, quotaCount,
                Round(remunerationBase / 12m), Round(remunerationBase * quotaFraction),
                $"Regra de {AdjustmentQuotaCalculator.MinimumDaysForQuota} dias; parâmetro SIPPES em meses/cotas", true));

        if (draft.VacationAdditionalEntitlement)
            components.Add(new AdjustmentComponentResult(
                "AR0096", "Adicional de férias", "Direito proporcional", true,
                "Direito explicitamente confirmado no ajuste.", remunerationBase, 100m / 3m, quotaCount,
                Round(remunerationBase / 3m / 12m), Round(remunerationBase / 3m * quotaFraction),
                $"1/3 da base, proporcional às cotas; parâmetro SIPPES em meses/cotas", true));

        if (draft.ChristmasEntitlement)
        {
            var christmasQuotas = Math.Clamp(draft.ChristmasQuotas, 0, 12);
            var christmas = Round(remunerationBase * christmasQuotas / 12m);
            if (draft.ReceivedChristmasFirstInstallment) christmas = Round(christmas - remunerationBase / 2m);
            components.Add(new AdjustmentComponentResult(
                "AR0070", "Adicional natalino", "Direito proporcional", true,
                "Direito explicitamente confirmado no ajuste.", remunerationBase, 0m, christmasQuotas,
                Round(remunerationBase / 12m), Math.Max(0m, christmas),
                "Quantidade de cotas de 13º informada separadamente", true));
        }

        if (draft.PecuniaryEntitlement)
        {
            var pecuniaryQuotas = Math.Clamp(draft.PecuniaryQuotas, 0, 50);
            components.Add(new AdjustmentComponentResult(
                "AR0066", "Compensação pecuniária", "Direito por remunerações", true,
                "Direito e quantidade explicitamente confirmados no ajuste.", remunerationBase, 0m, pecuniaryQuotas,
                remunerationBase, Round(remunerationBase * pecuniaryQuotas),
                "Lei nº 7.963/1989 — uma remuneração mensal por ano de efetivo serviço computável", true));
        }

        var parameters = new List<SippesParameter>();
        if (draft.VacationIndemnityEntitlement)
            parameters.Add(new SippesParameter("AR0094", "Indenização de férias", SippesParameterKind.QuantityOfMonths, quotaCount, 0m,
                "O número informado no SIPPES é quantidade de meses/cotas, não reais."));
        if (draft.VacationAdditionalEntitlement)
            parameters.Add(new SippesParameter("AR0096", "Adicional de férias", SippesParameterKind.QuantityOfMonths, quotaCount, 0m,
                "O número informado no SIPPES é quantidade de meses/cotas, não reais."));
        if (draft.PecuniaryEntitlement)
            parameters.Add(new SippesParameter("AR0066", "Compensação pecuniária", SippesParameterKind.QuantityOfYears,
                Math.Clamp(draft.PecuniaryQuotas, 0, 50), 0m,
                "Quantidade de remunerações/anos confirmada no processo; não é valor em reais."));

        var validations = AdjustmentValidationEngine.Validate(draft, remuneration, quotas, qualification, qualificationDecision);
        var memory = BuildMemory(draft, remuneration, quotas, qualification, qualificationDecision, salary,
            qualificationValue, militaryAdditional, availability, permanence, remunerationBase,
            minimumWage, minimumWageComplement, components, parameters);
        return new AdjustmentSimulationResult(draft, remuneration, quotas, components, parameters, validations, memory);
    }

    private static AdjustmentAccountsSettings MonthlySettings(
        AdjustmentDraft draft,
        decimal salary,
        int daysInMonth,
        int servedDays,
        decimal effectiveQualificationPercent)
        => new()
        {
            Salary = salary,
            DaysInMonth = Math.Clamp(daysInMonth, 1, 31),
            ServedDays = Math.Clamp(servedDays, 0, Math.Clamp(daysInMonth, 1, 31)),
            QualificationPercent = Math.Max(0m, effectiveQualificationPercent),
            MilitaryAdditionalPercent = Math.Max(0m, draft.MilitaryAdditionalPercent),
            MilitaryAvailabilityPercent = Math.Max(0m, draft.AvailabilityPercent),
            PermanencePercent = Math.Max(0m, draft.PermanencePercent),
            PreSchoolValue = Math.Max(0m, draft.PreSchoolValue),
            FamilySalaryValue = Math.Max(0m, draft.FamilySalaryValue),
            FusexPercent = Math.Max(0m, draft.FusexPercent),
            MilitaryPensionPercent = Math.Max(0m, draft.MilitaryPensionPercent),
            PnrPercent = Math.Max(0m, draft.PnrPercent),
            FusexDependentDiscount = Math.Max(0m, draft.FusexDependentDiscount),
            FusexMedicalExpense = Math.Max(0m, draft.FusexMedicalExpense),
            AlimonyValue = Math.Max(0m, draft.AlimonyValue),
            IncomeTaxDependents = Math.Max(0, draft.IncomeTaxDependents),
            IncludeMonthlyIncomeTax = draft.IncludeMonthlyIncomeTax,
            ApplyIncomeTaxReducer2026 = draft.ApplyIncomeTaxReducer2026,
            DeductFusexMedicalExpense = draft.DeductFusexMedicalExpense,
            DeductFusexDependent = draft.DeductFusexDependent,
            DeductAlimony = draft.DeductAlimony,
            DeductPnr = draft.DeductPnr,
            VacationAdditionalEntitlement = false,
            VacationIndemnityEntitlement = false,
            ChristmasEntitlement = false,
            IncludeVacationIncomeTax = false,
            IncludeChristmasIncomeTax = false
        };

    private static AdjustmentComponentResult MonthlyComponent(
        AdjustmentRubric row,
        AdjustmentDraft draft,
        QualificationOption qualification,
        EligibilityDecision qualificationDecision,
        decimal salary,
        decimal qualificationValue,
        int servedDays,
        int qualificationDays,
        int daysInMonth)
    {
        var isQualification = row.Code.Equals("AR0003", StringComparison.OrdinalIgnoreCase);
        var isEarning = row.Reference != "D" && row.Sign != "-";
        var percentage = row.Code.ToUpperInvariant() switch
        {
            "AR0003" => qualification.Percentage,
            "AR0014" => draft.MilitaryAdditionalPercent,
            "AR0170" => draft.AvailabilityPercent,
            "AR0004" => draft.PermanencePercent,
            "AD0001" => draft.FusexPercent,
            "AD0039" => draft.MilitaryPensionPercent,
            "AD0003" => draft.PnrPercent,
            _ => 0m
        };
        var quantity = isQualification ? qualificationDays : servedDays;
        var eligibility = isQualification ? qualificationDecision.IsEligible : true;
        var reason = isQualification
            ? qualificationDecision.Reason
            : $"Valor proporcional a {servedDays}/{daysInMonth} dia(s) da competência.";
        var basis = isQualification ? salary : row.FullValue;
        var unitValue = isQualification ? qualificationValue : row.FullValue;
        var origin = isQualification ? qualification.Source : "Cálculo diário central do Ajuste de Contas";
        return new AdjustmentComponentResult(row.Code, row.Description, isEarning ? "Rendimento mensal" : "Desconto mensal",
            eligibility, reason, basis, percentage, quantity, unitValue, row.ProportionalValue, origin, isEarning);
    }

    private static IReadOnlyList<string> BuildMemory(
        AdjustmentDraft draft,
        RemunerationReference remuneration,
        IReadOnlyList<AdjustmentQuotaDetail> quotas,
        QualificationOption qualification,
        EligibilityDecision qualificationDecision,
        decimal salary,
        decimal qualificationValue,
        decimal militaryAdditional,
        decimal availability,
        decimal permanence,
        decimal remunerationBase,
        decimal minimumWage,
        decimal minimumWageComplement,
        IReadOnlyList<AdjustmentComponentResult> components,
        IReadOnlyList<SippesParameter> parameters)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var lines = new List<string>
        {
            $"MILITAR: {draft.MilitaryName}",
            $"P/Grad atual: {draft.CurrentRank}",
            $"P/Grad real do direito: {draft.HistoricalRank}",
            $"Período efetivo: {AdjustmentRulesEngine.ResolveEffectiveEntitlementStart(draft):dd/MM/yyyy} a {draft.EntitlementEnd:dd/MM/yyyy}",
            $"Proporção mensal: {AdjustmentRulesEngine.ResolveServedDays(draft)}/{AdjustmentRulesEngine.ResolveDaysInMonth(draft)} dias",
            $"Tabela: {remuneration.TableVersion} — {remuneration.EffectivePeriod}",
            string.Empty,
            "COMPOSIÇÃO DA BASE",
            $"Soldo: {salary.ToString("C2", pt)}",
            $"Adicional militar ({draft.MilitaryAdditionalPercent:0.##}%): {militaryAdditional.ToString("C2", pt)}",
            $"Disponibilidade ({draft.AvailabilityPercent:0.##}%): {availability.ToString("C2", pt)}",
            $"Permanência ({draft.PermanencePercent:0.##}%): {permanence.ToString("C2", pt)}",
            $"Habilitação: {qualification.DisplayText}",
            $"Elegibilidade da habilitação: {qualificationDecision.Reason}",
            $"Adicional de habilitação: {qualificationValue.ToString("C2", pt)}",
            $"Base remuneratória: {remunerationBase.ToString("C2", pt)}",
            $"Salário mínimo vigente: {minimumWage.ToString("C2", pt)}",
            $"Complemento NR0026: {minimumWageComplement.ToString("C2", pt)} (fora da base de adicionais/descontos)",
            $"FUSEx automático: {draft.FusexPercent:0.##}%",
            $"Pensão militar automática: {draft.MilitaryPensionPercent:0.##}%",
            string.Empty,
            "PROPORCIONALIDADE"
        };
        lines.AddRange(quotas.Select(x => $"{x.MonthText}: {x.DaysText} — {x.QuotaText}. {x.Explanation}"));
        var quotaCount = AdjustmentRulesEngine.ResolveVacationQuotas(draft);
        var quotaSource = draft.VacationQuotasOverride.HasValue ? "quantidade informada manualmente" : "datas das cotas";
        lines.Add($"TOTAL: {quotaCount} cota(s) — proporção {quotaCount}/12 ({quotaSource})");
        lines.Add(string.Empty);
        lines.Add("RESULTADOS MONETÁRIOS");
        lines.AddRange(components.Where(x => x.CountsTowardTotal)
            .Select(x => $"{x.RubricCode} — {x.Description}: {x.MonetaryValue.ToString("C2", pt)}"));
        lines.Add(string.Empty);
        lines.Add("PARÂMETROS SIPPES (NÃO SÃO VALORES EM REAIS)");
        lines.AddRange(parameters.Select(x => $"{x.RubricCode}: {x.ParameterTypeText} = {x.Quantity}"));
        return lines;
    }

    private static decimal Percent(decimal basis, decimal percentage)
        => Round(Math.Max(0m, basis) * Math.Max(0m, percentage) / 100m);

    private static decimal Proportional(decimal fullValue, int servedDays, int daysInMonth)
        => daysInMonth <= 0 ? 0m : Round(Math.Max(0m, fullValue) * Math.Max(0, servedDays) / daysInMonth);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

public static class AdjustmentRemunerationResolver
{
    public static async Task<RemunerationReference> ResolveAsync(AdjustmentDraft draft, SalaryService salaryService, CancellationToken cancellationToken = default)
    {
        var date = AdjustmentRulesEngine.ResolveEffectiveEntitlementStart(draft) ?? draft.EntitlementEnd ?? DateTime.Today;
        if (draft.IsTestMode)
            return new RemunerationReference(
                MilitaryRankService.Canonicalize(draft.HistoricalRank),
                Math.Max(0m, draft.TestSalaryOverride ?? 0m),
                $"{AdjustmentRulesEngine.RulesVersion}|MODO-TESTE",
                "Valor manual de teste",
                "Sem efeito para boletim",
                date);
        var snapshot = await salaryService.GetOfficialSnapshotAsync(date, cancellationToken);
        var rank = MilitaryRankService.Canonicalize(draft.HistoricalRank);
        var salary = snapshot.Salaries.TryGetValue(rank, out var exact)
            ? exact
            : snapshot.Salaries.FirstOrDefault(x => MilitaryRankService.GetOrder(x.Key) == MilitaryRankService.GetOrder(rank)).Value;
        var version = $"{AdjustmentRulesEngine.RulesVersion}|SOLDO:{SalaryService.DescribeOfficialPeriod(date)}";
        return new RemunerationReference(rank, salary, version, snapshot.EffectivePeriod, snapshot.LegalBasis, date);
    }
}
