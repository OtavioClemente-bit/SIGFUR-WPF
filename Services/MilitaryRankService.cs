using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Ordem hierárquica oficial de exibição adotada pelo SIGFUR.
/// Reconhece nomes completos e abreviações usadas nos bancos antigos.
/// Também fornece uma paleta operacional discreta para leitura rápida do efetivo.
/// </summary>
public static partial class MilitaryRankService
{
    private sealed record RankDefinition(int Order, string Canonical, string ShortName, string Pattern, string RowColor);

    private static readonly RankDefinition[] Definitions =
    [
        new(1, "General de Exército", "Gen Ex", @"\bgeneral de exercito\b|\bgen ex\b", "#EDE9FE"),
        new(2, "General de Divisão", "Gen Div", @"\bgeneral de divisao\b|\bgen div\b", "#F3E8FF"),
        new(3, "General de Brigada", "Gen Bda", @"\bgeneral de brigada\b|\bgen bda\b", "#FAE8FF"),
        // O posto composto precisa vir antes de Coronel; do contrário "Tenente Coronel"
        // também casa com a palavra isolada "coronel".
        new(5, "Tenente Coronel", "Ten Cel", @"\btenente coronel\b|\bten cel\b|\btc\b", "#DBEAFE"),
        new(4, "Coronel", "Cel", @"\bcoronel\b|\bcel\b", "#E0E7FF"),
        new(6, "Major", "Maj", @"\bmajor\b|\bmaj\b", "#E0F2FE"),
        new(7, "Capitão", "Cap", @"\bcapitao\b|\bcap\b", "#CFFAFE"),
        new(8, "1º Tenente", "1º Ten", @"(?:^|\b)(?:1o|1|primeiro)\s*(?:tenente|ten)(?:\b|$)", "#CCFBF1"),
        new(9, "2º Tenente", "2º Ten", @"(?:^|\b)(?:2o|2|segundo)\s*(?:tenente|ten)(?:\b|$)", "#D1FAE5"),
        new(9, "Tenente", "Ten", @"(?:^|\b)(?:tenente|ten)(?:\b|$)", "#D1FAE5"),
        new(10, "Aspirante", "Asp", @"\baspirante\b|\basp\b", "#DCFCE7"),
        // Abrange Subtenente, Sub Ten, S Ten, ST e grafias antigas.
        new(11, "Subtenente", "S Ten", @"(?:^|\b)(?:subtenente|sub tenente|subten|sub ten|s ten|sten|st)(?:\b|$)", "#ECFCCB"),
        new(12, "1º Sargento", "1º Sgt", @"(?:^|\b)(?:1o|1|primeiro)\s*(?:sargento|sgt)(?:\b|$)", "#FEF3C7"),
        new(13, "2º Sargento", "2º Sgt", @"(?:^|\b)(?:2o|2|segundo)\s*(?:sargento|sgt)(?:\b|$)", "#FFEDD5"),
        new(14, "3º Sargento", "3º Sgt", @"(?:^|\b)(?:3o|3|terceiro)\s*(?:sargento|sgt)(?:\b|$)", "#FCE7F3"),
        // Padronização operacional adotada pela OM:
        // - "Cabo" e todas as variantes antigas de cabo passam a ser Cb Ef Profl;
        // - "Sd" genérico representa o soldado antigo/profissional;
        // - recruta, Sd Rcr, Sd EV e variantes equivalem a Sd Ef Vrv.
        new(15, "Cabo Efetivo Profissional", "Cb Ef Profl", @"\bcabo efetivo profissional\b|\bcb ef profl\b|\bcb ef prof\b|\bcabo profissional\b|\bcabo efetivo variavel\b|\bcb ef vrv\b|\bcb ev\b|\bcabo variavel\b|(?:^|\s)(?:cabo|cb)(?:$|\s)", "#F3E8FF"),
        new(16, "Soldado Efetivo Profissional", "Sd Ef Profl", @"\bsoldado efetivo profissional\b|\bsd ef profl\b|\bsd ef prof\b|\bsd profissional\b|\bsoldado antigo\b|\bsd antigo\b|^(?:soldado|sd)$", "#F1F5F9"),
        new(17, "Soldado Efetivo Variável", "Sd Ef Vrv", @"\bsoldado efetivo variavel\b|\bsd ef vrv\b|\bsd ev\b|\bsd variavel\b|\bsoldado recruta\b|\bsd rcr\b|\brecruta\b", "#F8FAFC")
    ];

    // Soldados recebem cor por ano de formação/incorporação para a leitura visual ficar imediata.
    private static readonly string[] SoldierYearPalette =
    [
        "#E0F2FE", "#DCFCE7", "#FEF3C7", "#FCE7F3", "#EDE9FE",
        "#FFEDD5", "#CCFBF1", "#E2E8F0", "#F3E8FF", "#ECFCCB"
    ];

    public static int GetOrder(string? rank)
    {
        var normalized = Normalize(rank);
        if (string.IsNullOrWhiteSpace(normalized)) return 999;
        foreach (var definition in Definitions)
            if (Regex.IsMatch(normalized, definition.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return definition.Order;
        return 999;
    }

    public static string Canonicalize(string? rank)
    {
        var original = (rank ?? string.Empty).Trim();
        var normalized = Normalize(original);
        foreach (var definition in Definitions)
            if (Regex.IsMatch(normalized, definition.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return definition.Canonical;
        return original;
    }

    public static string ShortName(string? rank)
    {
        var original = (rank ?? string.Empty).Trim();
        var normalized = Normalize(original);
        foreach (var definition in Definitions)
            if (Regex.IsMatch(normalized, definition.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return definition.ShortName;
        return string.IsNullOrWhiteSpace(original) ? "—" : original;
    }

    public static string GetAutomaticRowColor(string? rank, string? formationYear)
    {
        var order = GetOrder(rank);
        if (order is 16 or 17)
        {
            var digits = new string((formationYear ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length >= 2 && int.TryParse(digits[^Math.Min(4, digits.Length)..], out var year))
                return SoldierYearPalette[Math.Abs(year) % SoldierYearPalette.Length];
        }

        var normalized = Normalize(rank);
        foreach (var definition in Definitions)
            if (Regex.IsMatch(normalized, definition.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return definition.RowColor;
        return "#FFFFFF";
    }

    public static string GetColorDescription(string? rank, string? formationYear)
    {
        var shortRank = ShortName(rank);
        if (GetOrder(rank) is 16 or 17 && !string.IsNullOrWhiteSpace(formationYear))
            return $"{shortRank} — turma/ano {formationYear}";
        return shortRank;
    }

    public static IReadOnlyList<string> AllRanks { get; } = Definitions
        .OrderBy(x => x.Order)
        .Select(x => x.Canonical)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static IComparer<string> Comparer { get; } = new RankComparer();

    public static int Compare(string? leftRank, string? leftName, string? rightRank, string? rightName)
    {
        var rank = GetOrder(leftRank).CompareTo(GetOrder(rightRank));
        if (rank != 0) return rank;
        var canonical = string.Compare(Canonicalize(leftRank), Canonicalize(rightRank), StringComparison.CurrentCultureIgnoreCase);
        return canonical != 0 ? canonical : string.Compare(leftName, rightName, StringComparison.CurrentCultureIgnoreCase);
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(character switch
            {
                'º' or '°' or 'ª' => 'o',
                '.' or '-' or '/' or '_' or '[' or ']' or '(' or ')' or ':' or ';' => ' ',
                _ => char.ToLowerInvariant(character)
            });
        }
        return MultipleSpaces().Replace(builder.ToString(), " ").Trim();
    }

    private sealed class RankComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var order = GetOrder(x).CompareTo(GetOrder(y));
            return order != 0 ? order : string.Compare(Canonicalize(x), Canonicalize(y), StringComparison.CurrentCultureIgnoreCase);
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpaces();
}
