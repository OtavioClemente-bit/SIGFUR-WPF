namespace SIGFUR.Wpf.Services;

public sealed record MilitaryPreSchoolReference(decimal Ceiling, decimal MilitarySharePercent, decimal NetValue, string Category);

public static class MilitaryPreSchoolCatalog
{
    public const decimal CeilingFromMay2026 = 526.64m;
    public const decimal PreviousCeiling = 484.90m;
    public const decimal OfficerSharePercent = 10m;
    public const decimal EnlistedSharePercent = 5m;

    public static MilitaryPreSchoolReference Resolve(string? rank, DateTime referenceDate)
    {
        var ceiling = referenceDate.Date >= new DateTime(2026, 5, 1) ? CeilingFromMay2026 : PreviousCeiling;
        var officer = MilitaryRankService.GetOrder(rank) <= 10;
        var share = officer ? OfficerSharePercent : EnlistedSharePercent;
        var net = Math.Round(ceiling * (1m - share / 100m), 2, MidpointRounding.AwayFromZero);
        return new MilitaryPreSchoolReference(ceiling, share, net, officer ? "Oficial" : "Praça");
    }
}
