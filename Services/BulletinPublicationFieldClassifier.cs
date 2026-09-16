using System.Globalization;
using System.Text;

namespace SIGFUR.Wpf.Services;

public enum BulletinPublicationFieldKind { None, Bi, Adt, Generic }

public static class BulletinPublicationFieldClassifier
{
    public static BulletinPublicationFieldKind Classify(string? key)
    {
        var normalized = Normalize(key);
        if (string.IsNullOrWhiteSpace(normalized)) return BulletinPublicationFieldKind.None;
        if (normalized is "BAR" or "NUMEROBAR" or "BARREFERENCIA") return BulletinPublicationFieldKind.Adt;

        var hasAdt = normalized.Contains("ADT", StringComparison.Ordinal)
                     || normalized.Contains("ADITAMENTO", StringComparison.Ordinal);
        var hasBi = normalized == "BI"
                    || normalized.StartsWith("BI", StringComparison.Ordinal)
                    || normalized.EndsWith("BI", StringComparison.Ordinal)
                    || (normalized.Contains("BOLETIM", StringComparison.Ordinal)
                        && (normalized.Contains("REFERENCIA", StringComparison.Ordinal)
                            || normalized.Contains("PUBLICACAO", StringComparison.Ordinal)
                            || normalized.Contains("NUMERO", StringComparison.Ordinal)
                            || normalized.Contains("DATA", StringComparison.Ordinal)));

        if (hasAdt && !hasBi) return BulletinPublicationFieldKind.Adt;
        if (hasBi && !hasAdt) return BulletinPublicationFieldKind.Bi;
        if (hasBi && hasAdt) return BulletinPublicationFieldKind.Generic;

        var genericPublication = normalized.Contains("PUBLICACAO", StringComparison.Ordinal)
                                 || normalized is "DOCREFERENCIA" or "DOCUMENTOREFERENCIA"
                                     or "DOCAMPARO" or "DOCUMENTOAMPARO"
                                     or "REFERENCIAPUBLICACAO" or "REFERENCIABIADT";
        return genericPublication ? BulletinPublicationFieldKind.Generic : BulletinPublicationFieldKind.None;
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(decomposed
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark
                                && char.IsLetterOrDigit(character))
            .Select(char.ToUpperInvariant)
            .ToArray());
    }
}
