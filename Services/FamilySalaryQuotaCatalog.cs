namespace SIGFUR.Wpf.Services;

public static class FamilySalaryQuotaCatalog
{
    public const decimal MilitaryDependentQuota = 0.16m;

    public static decimal Resolve(int dependents)
        => Math.Round(Math.Max(0, dependents) * MilitaryDependentQuota, 2, MidpointRounding.AwayFromZero);
}
