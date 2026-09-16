using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class AdjustmentAccountsMigrationService
{
    public const int CurrentSchemaVersion = 4;

    public static AdjustmentAccountsStore Migrate(AdjustmentAccountsStore? source)
    {
        var store = source ?? new AdjustmentAccountsStore();
        store.Settings ??= new AdjustmentAccountsSettings();
        store.Rubrics ??= [];
        store.CustomBizuRules ??= [];
        store.Drafts ??= [];
        store.AuditTrail ??= [];

        if (store.CurrentDraft is null)
            store.CurrentDraft = FromLegacy(store.Settings);

        store.CurrentDraft.QualificationCode = QualificationCatalog.Resolve(store.CurrentDraft.QualificationCode).Code;
        store.SchemaVersion = CurrentSchemaVersion;
        return store;
    }

    public static AdjustmentDraft FromLegacy(AdjustmentAccountsSettings legacy)
    {
        var note = legacy.QualificationPercent > 0m
            ? $"Registro antigo continha habilitação livre de {legacy.QualificationPercent:0.##}%. Por segurança, o valor não foi aplicado; selecione uma opção estruturada, informe a vigência e confirme o documento."
            : "Registro antigo migrado. Confirme militar, período, situação e P/Grad histórico antes de gerar boletim.";

        return new AdjustmentDraft
        {
            MilitaryId = legacy.LastMilitaryId,
            CurrentRank = legacy.Rank,
            HistoricalRank = legacy.Rank,
            Situation = AdjustmentSituationKind.NotInformed,
            AdjustmentReason = legacy.BulletinReason,
            QualificationCode = QualificationCatalog.NoneCode,
            MilitaryAdditionalPercent = NonNegative(legacy.MilitaryAdditionalPercent),
            AvailabilityPercent = NonNegative(legacy.MilitaryAvailabilityPercent),
            PermanencePercent = NonNegative(legacy.PermanencePercent),
            VacationIndemnityEntitlement = legacy.VacationIndemnityEntitlement,
            VacationAdditionalEntitlement = legacy.VacationAdditionalEntitlement,
            ChristmasEntitlement = legacy.ChristmasEntitlement,
            ReceivedChristmasFirstInstallment = legacy.ReceivedChristmasFirstInstallment,
            PecuniaryEntitlement = legacy.PecuniaryEntitlement,
            PecuniaryQuotas = legacy.PecuniaryQuotas,
            ChristmasQuotas = Math.Clamp(legacy.ChristmasMonths, 0, 12),
            PreSchoolValue = NonNegative(legacy.PreSchoolValue),
            FamilySalaryValue = NonNegative(legacy.FamilySalaryValue),
            FusexPercent = NonNegative(legacy.FusexPercent),
            MilitaryPensionPercent = NonNegative(legacy.MilitaryPensionPercent),
            PnrPercent = NonNegative(legacy.PnrPercent),
            FusexDependentDiscount = NonNegative(legacy.FusexDependentDiscount),
            FusexMedicalExpense = NonNegative(legacy.FusexMedicalExpense),
            AlimonyValue = NonNegative(legacy.AlimonyValue),
            IncomeTaxDependents = Math.Max(0, legacy.IncomeTaxDependents),
            IncludeMonthlyIncomeTax = legacy.IncludeMonthlyIncomeTax,
            ApplyIncomeTaxReducer2026 = true,
            DeductFusexMedicalExpense = legacy.DeductFusexMedicalExpense,
            DeductFusexDependent = legacy.DeductFusexDependent,
            DeductAlimony = legacy.DeductAlimony,
            DeductPnr = legacy.DeductPnr,
            LegacyMigrationNote = note
        };
    }

    private static decimal NonNegative(decimal value) => Math.Max(0m, value);
}
