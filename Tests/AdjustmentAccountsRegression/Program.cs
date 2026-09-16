using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

static void Check(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException("Falha: " + scenario);
}

static AdjustmentBizuRule Rule(string title)
    => AdjustmentAccountsService.ResolveBizuRule(title)
       ?? throw new InvalidOperationException("Regra não encontrada: " + title);

var matrix = new[]
{
    ("Licenciamento Ex-Officio — Término de prorrogação Tp Sv", true, true, true, true),
    ("Licenciamento a pedido", true, true, true, false),
    ("Anulação de Incorporação", false, false, false, false),
    ("Desincorporação", false, false, true, false),
    ("Licenciamento a Bem da Disciplina (EP)", false, false, false, false),
    ("Exclusão a Bem da Disciplina (EV)", false, false, false, false),
    ("Deserção", false, false, false, false),
    ("Falecimento", true, true, false, false),
    ("Licenciamento Ex-Officio — Conveniência do serviço", true, true, true, false)
};

foreach (var (title, vacationAdditional, vacationIndemnity, christmas, pecuniaryAllowed) in matrix)
{
    var rule = Rule(title);
    Check(rule.VacationAdditional == vacationAdditional, title + " / adicional de férias");
    Check(rule.VacationIndemnity == vacationIndemnity, title + " / indenização de férias");
    Check(rule.ChristmasAdditional == christmas, title + " / adicional natalino");
    Check(rule.Pecuniary == pecuniaryAllowed, title + " / pecuniária");
}

Check(Rule("Revogação").Pecuniary is null, "revogação deve exigir conferência da pecuniária");
Check(Rule("Aprovação em concurso — Militar de carreira").Pecuniary is null,
    "concurso de carreira deve exigir conferência da pecuniária");
Check(Rule("Aprovação em concurso — Militar temporário").Pecuniary is null,
    "concurso temporário deve exigir conferência da pecuniária");
var gestante = Rule("Militar gestante acima de 8º ano");
Check(gestante.Pecuniary == true && gestante.PecuniaryQuotas == 9, "gestante acima do 8º ano / 9 pecuniárias");
var initialService = Rule("Sv Mil Inicial — arrimo/nascimento de filho");
Check(initialService.VacationAdditional == false && initialService.VacationIndemnity == false
      && initialService.ChristmasAdditional == false && initialService.Pecuniary == false,
    "serviço militar inicial especial não deve presumir estes direitos");

var customizedReason = Rule("Desincorporação").Clone();
customizedReason.IsCustom = true;
customizedReason.ChristmasAdditional = false;
customizedReason.Observation = "Regra personalizada para teste.";
var availableReasons = AdjustmentAccountsService.AvailableBizuRules([customizedReason]);
var resolvedCustomizedReason = AdjustmentAccountsService.ResolveBizuRule("desincorporação", [customizedReason]);
Check(resolvedCustomizedReason?.IsCustom == true && resolvedCustomizedReason.ChristmasAdditional == false,
    "regra personalizada deve substituir a regra padrão de mesmo título");
Check(availableReasons.Count == AdjustmentAccountsService.DefaultBizuRules().Count,
    "substituição personalizada não deve duplicar o motivo na janela de escolha");
Check(AdjustmentAccountsService.ResolveBizuRule("Licenciamento a pedido", [customizedReason])?.IsCustom == false,
    "demais motivos padrão devem continuar disponíveis junto das regras personalizadas");

var switching = new AdjustmentDraft
{
    VacationAdditionalEntitlement = true,
    VacationIndemnityEntitlement = true,
    ChristmasEntitlement = true,
    PecuniaryEntitlement = true,
    PecuniaryQuotas = 9
};
AdjustmentAccountsService.ApplyBizu(switching, Rule("Anulação de Incorporação"));
Check(!switching.VacationAdditionalEntitlement && !switching.VacationIndemnityEntitlement
      && !switching.ChristmasEntitlement && !switching.PecuniaryEntitlement && switching.PecuniaryQuotas == 0,
    "troca de motivo deve limpar direitos anteriores");

var monthly = new AdjustmentDraft
{
    MonthlyAdjustmentOnly = true,
    EntitlementEnd = new DateTime(2026, 8, 10)
};
Check(AdjustmentRulesEngine.ResolveEffectiveEntitlementStart(monthly) == new DateTime(2026, 8, 1),
    "ajuste mensal sem início deve começar no primeiro dia do mês final");

monthly.VacationQuotasOverride = 4;
Check(AdjustmentRulesEngine.ResolveVacationQuotas(monthly) == 4, "quantidade manual de cotas de férias");
monthly.VacationQuotasOverride = null;
monthly.VacationQuotaStart = new DateTime(2026, 1, 1);
monthly.VacationQuotaEnd = new DateTime(2026, 3, 31);
Check(AdjustmentRulesEngine.ResolveVacationQuotas(monthly) == 3, "cotas pelas datas próprias");
monthly.VacationQuotaEnd = null;
Check(AdjustmentRulesEngine.ResolveVacationQuotaEnd(monthly) == DateTime.Today,
    "fim opcional das cotas deve usar a data atual");
Check(FamilySalaryQuotaCatalog.Resolve(2) == 0.32m,
    "salário-família militar de R$ 0,16 por dependente");
Check(MilitaryPreSchoolCatalog.Resolve("3º Sargento", new DateTime(2026, 8, 1)).NetValue == 495.82m,
    "pré-escolar 2026 de praça com cota-parte de 5%");
Check(MilitaryPreSchoolCatalog.Resolve("Capitão", new DateTime(2026, 8, 1)).NetValue == 469.73m,
    "pré-escolar 2026 de oficial com cota-parte de 10%");

var exceptionalPercentage = new AdjustmentDraft
{
    HistoricalRank = "3º Sargento",
    Situation = AdjustmentSituationKind.Ordinary,
    MilitaryAdditionalPercent = 18m,
    MilitaryAdditionalPercentOverridden = true
};
MilitaryRemunerationCatalog.ApplyFixedPercentages(exceptionalPercentage);
Check(exceptionalPercentage.MilitaryAdditionalPercent == 18m,
    "percentual excepcional não pode ser sobrescrito pelo perfil automático");

var pecuniary = new AdjustmentDraft
{
    IsManualSimulation = true,
    MilitaryName = "OTAVIO MARTINS LOPES CLEMENTE",
    HistoricalRank = "3º Sgt",
    CurrentRank = "3º Sgt",
    Situation = AdjustmentSituationKind.Ordinary,
    MonthlyAdjustmentOnly = true,
    EntitlementEnd = new DateTime(2026, 8, 10),
    AdjustmentReason = "Licenciamento Ex-Officio — Término de prorrogação Tp Sv",
    VacationAdditionalEntitlement = false,
    VacationIndemnityEntitlement = false,
    ChristmasEntitlement = false,
    PecuniaryEntitlement = true,
    PecuniaryQuotas = 2,
    QualificationCode = QualificationCatalog.NoneCode
};
var remuneration = new RemunerationReference("3º Sgt", 1000m, "teste", "teste", "teste", new DateTime(2026, 8, 1));
var result = AdjustmentCalculationEngine.Simulate(pecuniary, remuneration);
var ar0066 = result.Components.Single(x => x.RubricCode == "AR0066");
Check(ar0066.Quantity == 2 && ar0066.MonetaryValue > 0m, "AR0066 com quantidade explícita");
Check(result.SippesParameters.Any(x => x.RubricCode == "AR0066" && x.ParameterKind == SippesParameterKind.QuantityOfYears && x.Quantity == 2),
    "parâmetro SIPPES da pecuniária");
Check(result.EffectiveEntitlementStart == new DateTime(2026, 8, 1) && result.ComputableDays == 10,
    "período mensal e quantidade de dias");

var forbidden = pecuniary.Clone();
forbidden.AdjustmentReason = "Anulação de Incorporação";
forbidden.PecuniaryEntitlement = false;
forbidden.PecuniaryQuotas = 0;
forbidden.VacationAdditionalEntitlement = true;
forbidden.VacationQuotasOverride = 1;
var forbiddenResult = AdjustmentCalculationEngine.Simulate(forbidden, remuneration);
Check(forbiddenResult.Validations.Any(x => x.Code == "RIGHT_FORBIDDEN_BY_REASON"),
    "validação deve impedir direito vedado pelo motivo");

var legacyAmbiguous = pecuniary.Clone();
legacyAmbiguous.AdjustmentReason = "Licenciamento a pedido / Anulação / Desincorporação";
legacyAmbiguous.PecuniaryEntitlement = false;
legacyAmbiguous.PecuniaryQuotas = 0;
var legacyResult = AdjustmentCalculationEngine.Simulate(legacyAmbiguous, remuneration);
Check(legacyResult.Validations.Any(x => x.Code == "REASON_NOT_CLASSIFIED"),
    "motivo legado ambíguo deve exigir nova seleção");

var military = new MilitaryRecord
{
    Rank = "3º Sgt",
    Name = "OTAVIO MARTINS LOPES CLEMENTE - OTAVIO",
    WarName = "OTAVIO"
};
Check(AdjustmentAccountsService.BulletinFullName(military) == "OTAVIO MARTINS LOPES CLEMENTE",
    "remover sufixo duplicado do nome de guerra");
Check(AdjustmentAccountsService.FormatBulletinMilitaryName(military).EndsWith("OTAVIO MARTINS LOPES CLEMENTE", StringComparison.Ordinal),
    "nome do boletim sem nome de guerra anexado");

var special = Rule("LTIP / LAC");
Check(special.VacationAdditional is null && special.VacationIndemnity is null
      && special.ChristmasAdditional is null && special.Pecuniary is null,
    "LTIP/LAC deve limpar direitos herdados e exigir conferência individual");
var specialDraft = switching.Clone();
specialDraft.VacationAdditionalEntitlement = true;
AdjustmentAccountsService.ApplyBizu(specialDraft, special);
Check(!specialDraft.VacationAdditionalEntitlement, "LTIP/LAC deve limpar direito anterior ao ser selecionado");

var halfMonth = new AdjustmentDraft
{
    IsManualSimulation = true,
    IsTestMode = true,
    MilitaryName = "TESTE",
    HistoricalRank = "3º Sgt",
    CurrentRank = "3º Sgt",
    Situation = AdjustmentSituationKind.Ordinary,
    MonthlyAdjustmentOnly = true,
    EntitlementEnd = new DateTime(2026, 4, 15),
    AdjustmentReason = "Outros / editar manualmente",
    QualificationCode = QualificationCatalog.FormationCode,
    TestSalaryOverride = 3000m,
    TestQualificationPercentOverride = 10m,
    TestDaysInMonthOverride = 30,
    TestServedDaysOverride = 15,
    MilitaryAdditionalPercent = 0m,
    AvailabilityPercent = 0m,
    PermanencePercent = 0m,
    FusexPercent = 3m,
    MilitaryPensionPercent = 9m,
    PnrPercent = 10m,
    PreSchoolValue = 300m,
    AlimonyValue = 300m,
    IncludeMonthlyIncomeTax = false
};
var halfReference = new RemunerationReference("3º Sgt", 3000m, "teste", "teste", "teste", new DateTime(2026, 4, 1));
var halfResult = AdjustmentCalculationEngine.Simulate(halfMonth, halfReference);
Check(halfResult.Components.Single(x => x.RubricCode == "AR0001").MonetaryValue == 1500m,
    "soldo de 15/30 dias");
Check(halfResult.Components.Single(x => x.RubricCode == "AR0003").MonetaryValue == 150m,
    "habilitação integral no período de 15/30 dias");
Check(halfResult.Components.Single(x => x.RubricCode == "AD0001").MonetaryValue == 49.50m,
    "FUSEx proporcional a 15/30 dias");
Check(halfResult.Components.Single(x => x.RubricCode == "AD0039").MonetaryValue == 148.50m,
    "pensão militar proporcional a 15/30 dias");
Check(halfResult.Components.Single(x => x.RubricCode == "AD0003").MonetaryValue == 150m,
    "PNR proporcional a 15/30 dias");
Check(halfResult.Components.Single(x => x.RubricCode == "AR0077").MonetaryValue == 150m,
    "pré-escolar proporcional a 15/30 dias");
Check(halfResult.Components.Single(x => x.RubricCode == "AD0014").MonetaryValue == 150m,
    "pensão alimentícia proporcional a 15/30 dias");
Check(!halfResult.CanGenerateBulletin && halfResult.Validations.Any(x => x.Code == "TEST_MODE"),
    "modo de teste deve permitir cálculo e bloquear boletim");

var partialQualification = halfMonth.Clone();
partialQualification.QualificationEffectiveDate = new DateTime(2026, 4, 10);
var partialResult = AdjustmentCalculationEngine.Simulate(partialQualification, halfReference);
var partialRow = partialResult.Components.Single(x => x.RubricCode == "AR0003");
Check(partialRow.Quantity == 6 && partialRow.MonetaryValue == 60m,
    "habilitação de 10 a 15 de abril deve calcular 6/30 dias");

var noProof = halfMonth.Clone();
noProof.IsTestMode = false;
noProof.TestSalaryOverride = null;
noProof.TestQualificationPercentOverride = null;
noProof.TestDaysInMonthOverride = null;
noProof.TestServedDaysOverride = null;
noProof.QualificationDocumentConfirmed = false;
noProof.QualificationEffectiveDate = new DateTime(2026, 4, 10);
noProof.MilitaryAdditionalPercent = 13m;
noProof.AvailabilityPercent = 5m;
var noProofResult = AdjustmentCalculationEngine.Simulate(noProof, halfReference);
Check(!noProofResult.Validations.Any(x => x.Code == "QUALIFICATION_INELIGIBLE"),
    "habilitação não deve exigir checkbox de comprovação");

var incomeTax = halfMonth.Clone();
incomeTax.TestSalaryOverride = 12000m;
incomeTax.TestQualificationPercentOverride = 0m;
incomeTax.QualificationCode = QualificationCatalog.NoneCode;
incomeTax.FusexPercent = 0m;
incomeTax.MilitaryPensionPercent = 0m;
incomeTax.PnrPercent = 0m;
incomeTax.PreSchoolValue = 0m;
incomeTax.AlimonyValue = 0m;
incomeTax.IncludeMonthlyIncomeTax = true;
incomeTax.ApplyIncomeTaxReducer2026 = true;
var incomeTaxReference = halfReference with { Salary = 12000m };
var incomeTaxResult = AdjustmentCalculationEngine.Simulate(incomeTax, incomeTaxReference);
Check(incomeTaxResult.Components.Single(x => x.RubricCode == "AD0010").MonetaryValue == 394.54m,
    "IRRF 2026 sobre rendimento proporcional de R$ 6.000,00");

Console.WriteLine("Ajuste de Contas: todos os testes de regressão passaram.");
