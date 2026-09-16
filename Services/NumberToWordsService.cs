using System.Globalization;
using System.Text.RegularExpressions;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Conversor numérico em português do Brasil. Aceita entradas como 1234,56,
/// 1.234,56, R$ 1.234,56 e 1234.56, sem depender da cultura configurada no Windows.
/// </summary>
public static partial class NumberToWordsService
{
    private const decimal MaximumValue = 999_999_999_999.99m;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly string[] Units = ["zero", "um", "dois", "três", "quatro", "cinco", "seis", "sete", "oito", "nove"];
    private static readonly string[] Teens = ["dez", "onze", "doze", "treze", "quatorze", "quinze", "dezesseis", "dezessete", "dezoito", "dezenove"];
    private static readonly string[] Tens = ["", "", "vinte", "trinta", "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa"];
    private static readonly string[] Hundreds = ["", "cento", "duzentos", "trezentos", "quatrocentos", "quinhentos", "seiscentos", "setecentos", "oitocentos", "novecentos"];

    public static string Convert(string? input, bool currency)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return TryParse(input, out var value)
            ? Convert(value, currency)
            : "Valor inválido. Digite, por exemplo, 1.234,56.";
    }

    public static string Convert(decimal value, bool currency)
    {
        if (Math.Abs(value) > MaximumValue)
            return "Valor acima do limite de 999.999.999.999,99.";

        var negative = value < 0;
        value = Math.Abs(decimal.Round(value, 2, MidpointRounding.AwayFromZero));
        var integer = decimal.ToInt64(decimal.Truncate(value));
        var decimalPart = decimal.ToInt32((value - integer) * 100m);

        string result;
        if (currency)
        {
            if (integer == 0 && decimalPart > 0)
            {
                result = $"{IntegerToWords(decimalPart)} {(decimalPart == 1 ? "centavo" : "centavos")}";
            }
            else
            {
                var deBeforeCurrency = integer >= 1_000_000 && integer % 1_000_000 == 0 ? " de" : string.Empty;
                result = $"{IntegerToWords(integer)}{deBeforeCurrency} {(integer == 1 ? "real" : "reais")}";
                if (decimalPart > 0)
                    result += $" e {IntegerToWords(decimalPart)} {(decimalPart == 1 ? "centavo" : "centavos")}";
            }
        }
        else
        {
            result = IntegerToWords(integer);
            if (decimalPart > 0)
            {
                var digits = decimalPart.ToString("00", CultureInfo.InvariantCulture);
                if (digits.EndsWith('0')) digits = digits[..1];
                result += " vírgula " + string.Join(" ", digits.Select(x => Units[x - '0']));
            }
        }

        return negative ? "menos " + result : result;
    }

    public static bool TryParse(string? input, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var text = input.Trim()
            .Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\u00A0", string.Empty)
            .Replace(" ", string.Empty);
        text = InvalidNumberChars().Replace(text, string.Empty);
        if (string.IsNullOrWhiteSpace(text) || text is "-" or "." or ",") return false;

        var negative = text.StartsWith('-');
        text = text.TrimStart('+', '-');
        if (string.IsNullOrWhiteSpace(text)) return false;

        var dotCount = text.Count(c => c == '.');
        var comma = text.LastIndexOf(',');
        var dot = text.LastIndexOf('.');
        var decimalIndex = -1;

        if (comma >= 0 && dot >= 0)
        {
            decimalIndex = Math.Max(comma, dot);
        }
        else if (comma >= 0)
        {
            // Na escrita brasileira a vírgula é sempre tratada como separador decimal.
            decimalIndex = comma;
        }
        else if (dot >= 0)
        {
            var digitsAfter = text.Length - dot - 1;
            // Um único ponto seguido por uma ou duas casas também é aceito como decimal.
            // Nos demais casos, os pontos são considerados separadores de milhar.
            if (dotCount == 1 && digitsAfter is 1 or 2) decimalIndex = dot;
        }

        string integerPart;
        string fractionPart;
        if (decimalIndex >= 0)
        {
            integerPart = text[..decimalIndex];
            fractionPart = text[(decimalIndex + 1)..];
        }
        else
        {
            integerPart = text;
            fractionPart = string.Empty;
        }

        integerPart = integerPart.Replace(".", string.Empty).Replace(",", string.Empty);
        fractionPart = fractionPart.Replace(".", string.Empty).Replace(",", string.Empty);
        if (integerPart.Length == 0) integerPart = "0";
        if (!integerPart.All(char.IsDigit) || !fractionPart.All(char.IsDigit)) return false;

        if (fractionPart.Length > 2) fractionPart = fractionPart[..2];
        fractionPart = fractionPart.PadRight(2, '0');
        var normalized = integerPart + (fractionPart.Length > 0 ? "." + fractionPart : string.Empty);
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (negative) value = -value;
        return Math.Abs(value) <= MaximumValue;
    }

    public static string FormatNumber(decimal value, bool currency)
        => currency ? value.ToString("C2", PtBr) : value.ToString("N2", PtBr);

    private static string IntegerToWords(long value)
    {
        if (value == 0) return Units[0];

        var parts = new List<(int GroupValue, string Text)>();
        AddGroup(1_000_000_000L, "bilhão", "bilhões");
        AddGroup(1_000_000L, "milhão", "milhões");
        AddGroup(1_000L, "mil", "mil");
        if (value > 0) parts.Add(((int)value, UpTo999((int)value)));
        return JoinGroups(parts);

        void AddGroup(long scale, string singular, string plural)
        {
            var amount = value / scale;
            if (amount <= 0) return;
            value %= scale;
            if (scale == 1_000 && amount == 1) parts.Add((1, "mil"));
            else parts.Add(((int)amount, $"{UpTo999((int)amount)} {(amount == 1 ? singular : plural)}"));
        }
    }

    private static string UpTo999(int value)
    {
        if (value == 0) return "zero";
        if (value == 100) return "cem";
        var parts = new List<string>();
        var hundreds = value / 100;
        if (hundreds > 0) parts.Add(Hundreds[hundreds]);
        value %= 100;
        if (value >= 10 && value <= 19) parts.Add(Teens[value - 10]);
        else
        {
            var tens = value / 10;
            var units = value % 10;
            if (tens > 0) parts.Add(Tens[tens]);
            if (units > 0) parts.Add(Units[units]);
        }
        return string.Join(" e ", parts);
    }

    private static string JoinGroups(IReadOnlyList<(int GroupValue, string Text)> parts)
    {
        if (parts.Count == 0) return "zero";
        if (parts.Count == 1) return parts[0].Text;

        if (parts.Count == 2)
        {
            var lower = parts[1].GroupValue;
            var separator = lower < 100 || lower % 100 == 0 ? " e " : " ";
            return parts[0].Text + separator + parts[1].Text;
        }

        var builder = new StringBuilder(parts[0].Text);
        for (var index = 1; index < parts.Count; index++)
        {
            var isLast = index == parts.Count - 1;
            var lower = parts[index].GroupValue;
            var separator = isLast && (lower < 100 || lower % 100 == 0) ? " e " : ", ";
            builder.Append(separator).Append(parts[index].Text);
        }
        return builder.ToString();
    }

    [GeneratedRegex("[^0-9,.+-]")]
    private static partial Regex InvalidNumberChars();
}
