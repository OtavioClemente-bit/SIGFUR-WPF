using System.Globalization;

namespace SIGFUR.Wpf.Services;

/// <summary>Manual Técnico do SIPPES, 17/07/2026, pp. 129–131.</summary>
public static class TransportCompetenceService
{
    public static DateTime BenefitForUnpaidPayroll(DateTime payroll)
        => new DateTime(payroll.Year, payroll.Month, 1).AddMonths(1);

    public static string Explain(DateTime payroll)
        => $"Folha sem recebimento: {payroll:MM/yyyy} → referência no SIPPES e no boletim: {BenefitForUnpaidPayroll(payroll):MM/yyyy}.";

    public static decimal Deduction(decimal monthlyNet, int workingDays, int blackDays, int redDays)
    {
        if (workingDays <= 0 || monthlyNet <= 0) return 0;
        var days = Math.Clamp(blackDays - redDays, 0, workingDays);
        return decimal.Round(monthlyNet * days / workingDays, 2, MidpointRounding.AwayFromZero);
    }
}
