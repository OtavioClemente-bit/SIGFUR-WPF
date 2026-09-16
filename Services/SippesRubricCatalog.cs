using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static partial class SippesRubricCatalog
{
    private static readonly Lazy<IReadOnlyList<SippesRubricRecord>> Cache = new(LoadInternal, LazyThreadSafetyMode.ExecutionAndPublication);

    private const string CompressedCatalog =
        "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" +
        "SNPM//whRTP9+T/+f0KOvS8=";

    public static IReadOnlyList<SippesRubricRecord> All => Cache.Value;

    public static IReadOnlyList<SippesRubricRecord> Search(string? term, string? filter, int limit = 2000)
    {
        var tokens = Normalize(term).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalizedFilter = Normalize(filter);
        var result = new List<SippesRubricRecord>();
        foreach (var item in All)
        {
            var prefix = item.Code.Length >= 2 ? item.Code[..2] : item.Code;
            if (!MatchesFilter(item, prefix, normalizedFilter)) continue;
            var searchable = Normalize($"{item.Code} {item.Description} {item.RubricType} {item.Nature} {item.Effect}");
            if (tokens.Length > 0 && tokens.Any(token => !searchable.Contains(token, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(item);
            if (result.Count >= Math.Max(1, limit)) break;
        }
        return result;
    }

    public static SippesRubricRecord? Find(string? code)
    {
        var wanted = (code ?? string.Empty).Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(wanted) ? null : All.FirstOrDefault(x => x.Code.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static AdjustmentRubric ToAdjustmentRubric(SippesRubricRecord source)
        => new()
        {
            Code = source.Code,
            Description = source.Description,
            Reference = source.Effect == "D" ? "D" : "R",
            Sign = source.Effect == "D" ? "-" : "+",
            Base = "MES",
            Kind = "FIXO",
            Value = 0m,
            IsIncluded = true,
            IsCustom = true
        };

    private static IReadOnlyList<SippesRubricRecord> LoadInternal()
    {
        try
        {
            var compressed = Convert.FromBase64String(CompressedCatalog);
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.UTF8);
            var json = reader.ReadToEnd();
            var rows = JsonSerializer.Deserialize<List<string[]>>(json) ?? [];
            var output = new List<SippesRubricRecord>(rows.Count);
            foreach (var row in rows)
            {
                if (row.Length < 4) continue;
                var code = (row[0] ?? string.Empty).Trim().ToUpperInvariant();
                var description = (row[1] ?? string.Empty).Trim();
                var rubricType = (row[2] ?? string.Empty).Trim();
                var nature = (row[3] ?? string.Empty).Trim();
                output.Add(new SippesRubricRecord
                {
                    Code = code,
                    Description = description,
                    RubricType = rubricType,
                    Nature = nature,
                    Effect = EffectFor(code, nature)
                });
            }
            return output;
        }
        catch
        {
            return [];
        }
    }

    private static string EffectFor(string code, string nature)
    {
        var prefix = code.Length >= 2 ? code[..2] : code;
        if (prefix is "NR" or "AR" or "DD" or "ER" or "FR") return "R";
        if (prefix is "ND" or "AD" or "FD" or "DR" or "ED") return "D";
        return Normalize(nature) == "RECEITA" ? "R" : "D";
    }

    private static bool MatchesFilter(SippesRubricRecord item, string prefix, string normalizedFilter)
    {
        if (string.IsNullOrWhiteSpace(normalizedFilter) || normalizedFilter == "TODOS") return true;
        if (normalizedFilter is "RECEITA" or "RENDIMENTO" or "PAGAMENTO") return item.Effect == "R";
        if (normalizedFilter is "DESPESA" or "DESCONTO") return item.Effect == "D";
        return normalizedFilter.Length == 2 && prefix.Equals(normalizedFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ');
        }
        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
