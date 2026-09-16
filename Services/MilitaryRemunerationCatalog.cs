using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public enum QualificationSelectionMode
{
    NotDue,
    FixedFormation,
    FormationAfterStage,
    SelectableCourse
}

public sealed record MilitaryRemunerationProfile(
    string CanonicalRank,
    decimal MilitaryAdditionalPercent,
    decimal AvailabilityPercent,
    decimal FusexPercent,
    decimal MilitaryPensionPercent,
    bool ReceivesMinimumWageComplement,
    QualificationSelectionMode QualificationMode,
    string Explanation)
{
    public bool IsQualificationFixed => QualificationMode is QualificationSelectionMode.NotDue or QualificationSelectionMode.FixedFormation;

    public bool IsQualificationAllowed(string code)
    {
        var resolved = QualificationCatalog.Resolve(code);
        return QualificationMode switch
        {
            QualificationSelectionMode.NotDue => resolved.IsNone,
            QualificationSelectionMode.FixedFormation => resolved.Code == QualificationCatalog.FormationCode,
            QualificationSelectionMode.FormationAfterStage => resolved.IsNone || resolved.Code == QualificationCatalog.FormationCode,
            _ => true
        };
    }
}

/// <summary>
/// Centraliza os percentuais legais que incidem sobre o soldo. O adicional militar vem da
/// MP nº 2.215-10/2001 (Anexo II, Tabela II), a disponibilidade da Lei nº 13.954/2019
/// (Anexo II) e a habilitação da mesma Lei (Anexo III).
/// </summary>
public static class MilitaryRemunerationCatalog
{
    public const string LegalBasis = "MP nº 2.215-10/2001, Anexo II; Lei nº 13.954/2019, Anexos II e III";

    public static MilitaryRemunerationProfile Resolve(
        string? rank,
        AdjustmentSituationKind situation = AdjustmentSituationKind.Ordinary,
        DateTime? referenceDate = null)
    {
        var canonical = MilitaryRankService.Canonicalize(rank);
        var pension = MilitaryPensionPercentAt(referenceDate ?? DateTime.Today);

        if (canonical == "Soldado Efetivo Variável")
        {
            if (situation == AdjustmentSituationKind.RecruitSpecialExtension)
                return Profile(canonical, 13m, 5m, QualificationSelectionMode.FixedFormation,
                    "Dilação/situação 077: remuneração de Soldado Engajado, conforme o Manual Técnico do SIPPES.",
                    pension: pension, fusex: 0m, minimumWageComplement: true);
            return Profile(canonical, 0m, 5m, QualificationSelectionMode.NotDue,
                "Serviço militar inicial: sem adicional militar e sem habilitação; disponibilidade de 5%.",
                pension: pension, fusex: 0m, minimumWageComplement: true);
        }

        if (canonical == "Soldado Efetivo Profissional")
            return Profile(canonical, 13m, 5m, QualificationSelectionMode.FixedFormation,
                "Soldado engajado/profissional: formação 12%, adicional militar 13% e disponibilidade 5%.", pension: pension);
        if (canonical == "Cabo Efetivo Profissional")
            return Profile(canonical, 13m, 6m, QualificationSelectionMode.FixedFormation,
                "Cabo profissional: formação 12%, adicional militar 13% e disponibilidade 6%.", pension: pension);

        if (canonical == "Aspirante")
        {
            if (situation == AdjustmentSituationKind.AspirantEipot)
                return Profile(canonical, 19m, 5m, QualificationSelectionMode.NotDue,
                    "EIPOT: o campo de habilitação permanece em branco até a conclusão do estágio.", pension: pension);
            if (situation == AdjustmentSituationKind.AspirantEic)
                return Profile(canonical, 19m, 5m, QualificationSelectionMode.FixedFormation,
                    "EIC: formação de 12% a partir da convocação/início comprovado do EIC.", pension: pension);
            if (situation == AdjustmentSituationKind.AspirantOtt)
                return Profile(canonical, 19m, 5m, QualificationSelectionMode.FormationAfterStage,
                    "OTT: formação de 12% somente depois dos primeiros 45 dias.", pension: pension);
            return Profile(canonical, 19m, 5m, QualificationSelectionMode.SelectableCourse,
                "Aspirante: adicional militar e disponibilidade fixos; habilitação depende do curso e da vigência.", pension: pension);
        }

        var (military, availability) = canonical switch
        {
            "General de Exército" => (28m, 41m),
            "General de Divisão" => (28m, 38m),
            "General de Brigada" => (28m, 35m),
            "Coronel" => (25m, 32m),
            "Tenente Coronel" => (25m, 26m),
            "Major" => (25m, 20m),
            "Capitão" => (22m, 12m),
            "1º Tenente" => (19m, 6m),
            "2º Tenente" => (19m, 5m),
            "Tenente" => (19m, 5m),
            "Subtenente" => (16m, 32m),
            "1º Sargento" => (16m, 20m),
            "2º Sargento" => (16m, 12m),
            "3º Sargento" => (16m, 6m),
            _ => (0m, 0m)
        };

        return Profile(canonical, military, availability, QualificationSelectionMode.SelectableCourse,
            military == 0m && availability == 0m
                ? "P/Grad sem correspondência no catálogo legal; exige conferência."
                : "Adicional militar e disponibilidade definidos pelo P/Grad; selecione o curso de habilitação aplicável.",
            pension: pension);
    }

    public static AdjustmentSituationKind SuggestedSituation(string? rank)
        => MilitaryRankService.Canonicalize(rank) switch
        {
            "Soldado Efetivo Variável" => AdjustmentSituationKind.RecruitOrdinary,
            "Soldado Efetivo Profissional" => AdjustmentSituationKind.EngagedSoldier,
            "Aspirante" => AdjustmentSituationKind.NotInformed,
            _ => AdjustmentSituationKind.Ordinary
        };

    public static void ApplyFixedPercentages(AdjustmentDraft draft)
    {
        var profile = Resolve(draft.HistoricalRank, draft.Situation, draft.EntitlementStart);
        if (!draft.MilitaryAdditionalPercentOverridden) draft.MilitaryAdditionalPercent = profile.MilitaryAdditionalPercent;
        if (!draft.AvailabilityPercentOverridden) draft.AvailabilityPercent = profile.AvailabilityPercent;
        if (!draft.FusexPercentOverridden) draft.FusexPercent = profile.FusexPercent;
        if (!draft.MilitaryPensionPercentOverridden) draft.MilitaryPensionPercent = profile.MilitaryPensionPercent;
        draft.PnrPercent = 0m;
        if (profile.FusexPercent == 0m)
        {
            draft.FusexDependentDiscount = 0m;
            draft.FusexMedicalExpense = 0m;
        }
        if (profile.QualificationMode == QualificationSelectionMode.FixedFormation)
        {
            draft.QualificationCode = QualificationCatalog.FormationCode;
            draft.QualificationEffectiveDate ??= draft.EntitlementStart;
        }
    }

    public static IReadOnlyList<QualificationOption> QualificationOptions(MilitaryRemunerationProfile profile)
        => profile.QualificationMode switch
        {
            QualificationSelectionMode.NotDue => [QualificationCatalog.All[0]],
            QualificationSelectionMode.FixedFormation => [QualificationCatalog.Resolve(QualificationCatalog.FormationCode)],
            QualificationSelectionMode.FormationAfterStage => QualificationCatalog.All.Where(x => x.IsNone || x.Code == QualificationCatalog.FormationCode).ToList(),
            _ => QualificationCatalog.All
        };

    private static MilitaryRemunerationProfile Profile(
        string rank,
        decimal military,
        decimal availability,
        QualificationSelectionMode qualification,
        string explanation,
        decimal pension = 10.5m,
        decimal fusex = 3m,
        bool minimumWageComplement = false)
        => new(rank, military, availability, fusex, pension, minimumWageComplement, qualification, explanation);

    public static decimal MilitaryPensionPercentAt(DateTime referenceDate)
        => referenceDate.Date switch
        {
            var date when date < new DateTime(2020, 1, 1) => 7.5m,
            var date when date < new DateTime(2021, 1, 1) => 9.5m,
            _ => 10.5m
        };
}

public static class MinimumWageCatalog
{
    public const string LegalBasis = "Art. 18 da MP nº 2.215-10/2001 e salário mínimo nacional vigente";

    public static decimal Resolve(DateTime referenceDate)
        => referenceDate.Date switch
        {
            var date when date >= new DateTime(2026, 1, 1) => 1621m,
            var date when date >= new DateTime(2025, 1, 1) => 1518m,
            var date when date >= new DateTime(2024, 1, 1) => 1412m,
            var date when date >= new DateTime(2023, 5, 1) => 1320m,
            var date when date >= new DateTime(2023, 1, 1) => 1302m,
            var date when date >= new DateTime(2022, 1, 1) => 1212m,
            var date when date >= new DateTime(2021, 1, 1) => 1100m,
            var date when date >= new DateTime(2020, 2, 1) => 1045m,
            var date when date >= new DateTime(2020, 1, 1) => 1039m,
            _ => 998m
        };
}
