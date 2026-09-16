using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Identifica bancos pelo código COMPE para que pequenas diferenças de pontuação
/// ou razão social não criem opções duplicadas no cadastro.
/// </summary>
public static partial class MilitaryBankService
{
    public static string IdentityKey(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var code = BankCodeRegex().Match(text);
        if (code.Success) return "COMPE:" + code.Groups[1].Value;

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var normalized = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) normalized.Append(char.ToUpperInvariant(character));
        }
        return normalized.Length == 0 ? string.Empty : "NOME:" + normalized;
    }

    public static bool SameBank(string? left, string? right)
    {
        var leftKey = IdentityKey(left);
        return !string.IsNullOrWhiteSpace(leftKey) && leftKey.Equals(IdentityKey(right), StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> Consolidate(IEnumerable<string?> values)
    {
        var result = new List<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            var value = (raw ?? string.Empty).Trim();
            var key = IdentityKey(value);
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(key) || !keys.Add(key)) continue;
            result.Add(value);
        }
        return result;
    }

    public static int SortCode(string? value)
    {
        var match = BankCodeRegex().Match(value ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var code) ? code : int.MaxValue;
    }

    [GeneratedRegex(@"(?:^|\D)(\d{3})(?:\D|$)", RegexOptions.CultureInvariant)]
    private static partial Regex BankCodeRegex();
}
