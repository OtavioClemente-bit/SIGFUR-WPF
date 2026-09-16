namespace SIGFUR.Wpf.Services;

public static class MilitaryCareerTypeService
{
    public const string Career = "Carreira";
    public const string Temporary = "Temporário";

    public static string Normalize(string? value)
    {
        var normalized = MilitaryRankService.Normalize(value);
        if (normalized.Contains("tempor", StringComparison.Ordinal)) return Temporary;
        if (normalized.Contains("carreira", StringComparison.Ordinal)) return Career;
        return string.Empty;
    }

    public static string Display(string? value)
        => Normalize(value) is { Length: > 0 } result ? result : "Não informado";

    public static string? SuggestForRank(string? rank)
        => MilitaryRankService.Canonicalize(rank) switch
        {
            "Soldado Efetivo Variável" or "Soldado Efetivo Profissional" or "Cabo Efetivo Profissional" => Temporary,
            _ => Career
        };
}
