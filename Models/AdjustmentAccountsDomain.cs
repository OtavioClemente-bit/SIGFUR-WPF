using System.Globalization;
using System.Text.Json.Serialization;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public enum AdjustmentValidationSeverity
{
    Information,
    Warning,
    BlockingError
}

public enum AdjustmentSituationKind
{
    NotInformed,
    Ordinary,
    RecruitOrdinary,
    RecruitSpecialExtension,
    EngagedSoldier,
    AspirantEipot,
    AspirantEic,
    AspirantOtt,
    OtherRequiresReview
}

public enum SippesParameterKind
{
    QuantityOfMonths,
    QuantityOfYears,
    QuantityOfDays,
    MonetaryAmount
}

public sealed record QualificationOption(
    string Code,
    string Description,
    decimal Percentage,
    DateTime? EffectiveFrom,
    DateTime? EffectiveTo,
    string RubricCode,
    string Source)
{
    public bool IsNone => Percentage == 0m;
    public string DisplayText => IsNone ? Description : $"{Description} — {Percentage:0.##}%";
}

public sealed class AdjustmentDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int MilitaryId { get; set; }
    public bool IsManualSimulation { get; set; }
    public bool IsTestMode { get; set; }
    public string MilitaryName { get; set; } = string.Empty;
    public string CurrentRank { get; set; } = string.Empty;
    public string HistoricalRank { get; set; } = string.Empty;
    public DateTime? EntitlementStart { get; set; }
    public DateTime? EntitlementEnd { get; set; }
    public bool MonthlyAdjustmentOnly { get; set; } = true;
    public DateTime? EnlistmentDate { get; set; }
    public DateTime? VacationQuotaStart { get; set; }
    public DateTime? VacationQuotaEnd { get; set; }
    public int? VacationQuotasOverride { get; set; }
    public AdjustmentSituationKind Situation { get; set; }
    public string AdjustmentReason { get; set; } = string.Empty;
    public string QualificationCode { get; set; } = QualificationCatalog.NoneCode;
    public DateTime? QualificationEffectiveDate { get; set; }
    // Mantido apenas para compatibilidade com rascunhos antigos. A habilitação
    // selecionada não depende mais desta confirmação.
    public bool QualificationDocumentConfirmed { get; set; }
    public decimal? TestSalaryOverride { get; set; }
    public decimal? TestQualificationPercentOverride { get; set; }
    public int? TestDaysInMonthOverride { get; set; }
    public int? TestServedDaysOverride { get; set; }
    public decimal MilitaryAdditionalPercent { get; set; }
    public decimal AvailabilityPercent { get; set; }
    public decimal PermanencePercent { get; set; }
    public bool VacationIndemnityEntitlement { get; set; }
    public bool VacationAdditionalEntitlement { get; set; }
    public bool ChristmasEntitlement { get; set; }
    public bool ReceivedChristmasFirstInstallment { get; set; }
    public bool PecuniaryEntitlement { get; set; }
    public int PecuniaryQuotas { get; set; }
    public int ChristmasQuotas { get; set; }
    public decimal PreSchoolValue { get; set; }
    public int FamilySalaryDependents { get; set; }
    public decimal FamilySalaryValue { get; set; }
    public decimal FusexPercent { get; set; }
    public decimal MilitaryPensionPercent { get; set; }
    public decimal PnrPercent { get; set; }
    public decimal FusexDependentDiscount { get; set; }
    public decimal FusexMedicalExpense { get; set; }
    public decimal AlimonyValue { get; set; }
    public bool MilitaryAdditionalPercentOverridden { get; set; }
    public bool AvailabilityPercentOverridden { get; set; }
    public bool FusexPercentOverridden { get; set; }
    public bool MilitaryPensionPercentOverridden { get; set; }
    public int IncomeTaxDependents { get; set; }
    public bool IncludeMonthlyIncomeTax { get; set; } = true;
    public bool ApplyIncomeTaxReducer2026 { get; set; } = true;
    public bool DeductFusexMedicalExpense { get; set; }
    public bool DeductFusexDependent { get; set; } = true;
    public bool DeductAlimony { get; set; }
    public bool DeductPnr { get; set; }
    public string RemunerationTableVersion { get; set; } = string.Empty;
    public string LegacyMigrationNote { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public AdjustmentDraft Clone()
        => JsonSerializer.Deserialize<AdjustmentDraft>(JsonSerializer.Serialize(this)) ?? new AdjustmentDraft();
}

public sealed record AdjustmentQuotaDetail(
    int Year,
    int Month,
    int ComputableDays,
    int DaysInMonth,
    bool IsFullMonth,
    bool CountsAsQuota,
    string Explanation)
{
    [JsonIgnore]
    public string MonthText => new DateTime(Year, Month, 1).ToString("MMM/yy", CultureInfo.GetCultureInfo("pt-BR")).ToUpperInvariant();

    [JsonIgnore]
    public string DaysText => IsFullMonth ? "mês completo" : $"{ComputableDays} dia(s) computável(is)";

    [JsonIgnore]
    public string QuotaText => CountsAsQuota ? "✓ 1 cota" : "— não computa";
}

public sealed record SippesParameter(
    string RubricCode,
    string Description,
    SippesParameterKind ParameterKind,
    int Quantity,
    decimal MonetaryAmount,
    string Explanation)
{
    [JsonIgnore]
    public string ParameterTypeText => ParameterKind switch
    {
        SippesParameterKind.QuantityOfMonths => "Quantidade de meses/cotas",
        SippesParameterKind.QuantityOfYears => "Quantidade de anos/remunerações",
        SippesParameterKind.QuantityOfDays => "Quantidade de dias",
        _ => "Valor monetário"
    };

    [JsonIgnore]
    public string ParameterValueText => ParameterKind == SippesParameterKind.MonetaryAmount
        ? AdjustmentAccountsService.FormatMoney(MonetaryAmount)
        : Quantity.ToString(CultureInfo.InvariantCulture);
}

public sealed record AdjustmentComponentResult(
    string RubricCode,
    string Description,
    string ComponentType,
    bool IsEligible,
    string EligibilityReason,
    decimal CalculationBase,
    decimal Percentage,
    int Quantity,
    decimal UnitValue,
    decimal MonetaryValue,
    string RuleOrigin,
    bool IsEarning,
    bool CountsTowardTotal = true)
{
    [JsonIgnore] public string CalculationBaseText => AdjustmentAccountsService.FormatMoney(CalculationBase);
    [JsonIgnore] public string PercentageText => Percentage == 0m ? "—" : $"{Percentage:0.##}%";
    [JsonIgnore] public string QuantityText => Quantity == 0 ? "—" : Quantity.ToString(CultureInfo.InvariantCulture);
    [JsonIgnore] public string UnitValueText => AdjustmentAccountsService.FormatMoney(UnitValue);
    [JsonIgnore] public string MonetaryValueText => AdjustmentAccountsService.FormatMoney(MonetaryValue);
    [JsonIgnore] public string EligibilityText => IsEligible ? "Elegível" : "Não elegível";
}

public sealed record AdjustmentValidationMessage(
    AdjustmentValidationSeverity Severity,
    string Code,
    string Message,
    string Field = "")
{
    [JsonIgnore]
    public string SeverityText => Severity switch
    {
        AdjustmentValidationSeverity.BlockingError => "ERRO",
        AdjustmentValidationSeverity.Warning => "AVISO",
        _ => "INFO"
    };
}

public sealed record RemunerationReference(
    string Rank,
    decimal Salary,
    string TableVersion,
    string EffectivePeriod,
    string LegalBasis,
    DateTime ReferenceDate);

public sealed class AdjustmentSimulationResult
{
    public AdjustmentSimulationResult(
        AdjustmentDraft draft,
        RemunerationReference remuneration,
        IEnumerable<AdjustmentQuotaDetail> quotaDetails,
        IEnumerable<AdjustmentComponentResult> components,
        IEnumerable<SippesParameter> sippesParameters,
        IEnumerable<AdjustmentValidationMessage> validations,
        IEnumerable<string> calculationMemory)
    {
        _draft = draft.Clone();
        Remuneration = remuneration;
        QuotaDetails = quotaDetails.ToList().AsReadOnly();
        Components = components.ToList().AsReadOnly();
        SippesParameters = sippesParameters.ToList().AsReadOnly();
        Validations = validations.ToList().AsReadOnly();
        CalculationMemory = calculationMemory.ToList().AsReadOnly();
    }

    private readonly AdjustmentDraft _draft;
    public AdjustmentDraft Draft => _draft.Clone();
    public RemunerationReference Remuneration { get; }
    public IReadOnlyList<AdjustmentQuotaDetail> QuotaDetails { get; }
    public IReadOnlyList<AdjustmentComponentResult> Components { get; }
    public IReadOnlyList<SippesParameter> SippesParameters { get; }
    public IReadOnlyList<AdjustmentValidationMessage> Validations { get; }
    public IReadOnlyList<string> CalculationMemory { get; }
    public int VacationQuotas => _draft.VacationQuotasOverride ?? QuotaDetails.Count(x => x.CountsAsQuota);
    public DateTime? EffectiveEntitlementStart => AdjustmentRulesEngine.ResolveEffectiveEntitlementStart(_draft);
    public int DaysInMonth => AdjustmentRulesEngine.ResolveDaysInMonth(_draft);
    public int ComputableDays => AdjustmentRulesEngine.ResolveServedDays(_draft);
    public decimal Earnings => Round(Components.Where(x => x.IsEligible && x.CountsTowardTotal && x.IsEarning).Sum(x => x.MonetaryValue));
    public decimal Discounts => Round(Components.Where(x => x.IsEligible && x.CountsTowardTotal && !x.IsEarning).Sum(x => x.MonetaryValue));
    public decimal Net => Round(Earnings - Discounts);
    public bool HasBlockingErrors => Validations.Any(x => x.Severity == AdjustmentValidationSeverity.BlockingError);
    public bool CanGenerateBulletin => !HasBlockingErrors && Draft.MilitaryId > 0 && !Draft.IsManualSimulation && !Draft.IsTestMode;
    public string MemoryText => string.Join(Environment.NewLine, CalculationMemory);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

public sealed class AdjustmentAuditEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Action { get; set; } = string.Empty;
    public string AdjustmentId { get; set; } = string.Empty;
    public int MilitaryId { get; set; }
    public string MilitaryName { get; set; } = string.Empty;
    public string HistoricalRank { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public string RulesVersion { get; set; } = AdjustmentRulesEngine.RulesVersion;
    public string RemunerationTableVersion { get; set; } = string.Empty;
    public List<string> Alerts { get; set; } = [];
    public string GeneratedDocument { get; set; } = string.Empty;
}

public static class QualificationCatalog
{
    public const string NoneCode = "NONE";
    public const string FormationCode = "HAB_FORMACAO";
    public const string SpecializationCode = "HAB_ESPECIALIZACAO";
    public const string ImprovementCode = "HAB_APERFEICOAMENTO";
    public const string HighStudiesTwoCode = "HAB_ALTOS_ESTUDOS_II";
    public const string HighStudiesOneCode = "HAB_ALTOS_ESTUDOS_I";
    public const string CatalogSource = "Lei nº 13.954/2019, Anexo III — tabela de adicional de habilitação";

    public static IReadOnlyList<QualificationOption> All { get; } =
    [
        new(NoneCode, "Não possui / ainda não adquiriu no período", 0m, null, null, string.Empty, "Manual Técnico do SIPPES"),
        Option(FormationCode, "Formação", 12m),
        Option(SpecializationCode, "Especialização", 27m),
        Option(ImprovementCode, "Aperfeiçoamento", 45m),
        Option(HighStudiesTwoCode, "Altos Estudos — Categoria II", 68m),
        Option(HighStudiesOneCode, "Altos Estudos — Categoria I", 73m)
    ];

    public static QualificationOption Resolve(string? code, DateTime? referenceDate = null)
    {
        var normalizedCode = NormalizeLegacyCode(code);
        var option = All.FirstOrDefault(x => x.Code.Equals(normalizedCode, StringComparison.OrdinalIgnoreCase)) ?? All[0];
        if (option.IsNone || referenceDate is null) return option;
        return option with { Percentage = PercentageAt(option.Code, referenceDate.Value) };
    }

    public static bool IsLegalPercentage(decimal percentage)
        => All.Any(x => x.Percentage == percentage);

    public static decimal PercentageAt(string code, DateTime referenceDate)
    {
        var stage = referenceDate.Date switch
        {
            var date when date < new DateTime(2020, 7, 1) => 0,
            var date when date < new DateTime(2021, 7, 1) => 1,
            var date when date < new DateTime(2022, 7, 1) => 2,
            var date when date < new DateTime(2023, 7, 1) => 3,
            _ => 4
        };
        return NormalizeLegacyCode(code) switch
        {
            FormationCode => 12m,
            SpecializationCode => new decimal[] { 16m, 19m, 22m, 25m, 27m }[stage],
            ImprovementCode => new decimal[] { 20m, 27m, 34m, 41m, 45m }[stage],
            HighStudiesTwoCode => new decimal[] { 25m, 37m, 49m, 61m, 68m }[stage],
            HighStudiesOneCode => new decimal[] { 30m, 42m, 54m, 66m, 73m }[stage],
            _ => 0m
        };
    }

    private static string NormalizeLegacyCode(string? code)
        => (code ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "HAB_12" => FormationCode,
            "HAB_27" => SpecializationCode,
            "HAB_45" => ImprovementCode,
            "HAB_68" => HighStudiesTwoCode,
            "HAB_73" => HighStudiesOneCode,
            var value => value
        };

    private static QualificationOption Option(string code, string description, decimal percentage)
        => new(code, description, percentage, new DateTime(2023, 7, 1), null, "AR0003", CatalogSource);
}

public static class AdjustmentSituationCatalog
{
    public static IReadOnlyDictionary<AdjustmentSituationKind, string> All { get; } = new Dictionary<AdjustmentSituationKind, string>
    {
        [AdjustmentSituationKind.NotInformed] = "Selecione a situação no período",
        [AdjustmentSituationKind.Ordinary] = "Situação remuneratória ordinária",
        [AdjustmentSituationKind.RecruitOrdinary] = "Recruta / Sd EV comum",
        [AdjustmentSituationKind.RecruitSpecialExtension] = "Recruta em dilação / situação especial",
        [AdjustmentSituationKind.EngagedSoldier] = "Soldado engajado / efetivo profissional",
        [AdjustmentSituationKind.AspirantEipot] = "Aspirante em EIPOT",
        [AdjustmentSituationKind.AspirantEic] = "Aspirante em EIC",
        [AdjustmentSituationKind.AspirantOtt] = "Aspirante convocado / OTT",
        [AdjustmentSituationKind.OtherRequiresReview] = "Outra situação — exige conferência"
    };
}
