using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class BulletinTableClipboardImporter
{
    private static readonly Regex TableRegex = new(@"<table\b[^>]*>.*?</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RowRegex = new(@"<tr\b[^>]*>(?<body>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CellRegex = new(@"<t(?<kind>d|h)\b(?<attrs>[^>]*)>(?<body>.*?)</t(?:d|h)>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public static bool TryReadClipboard(out BulletinTableData table, out string source)
    {
        table = BulletinTableCodec.Create();
        source = string.Empty;
        try
        {
            if (Clipboard.ContainsData(DataFormats.Html)
                && Clipboard.GetData(DataFormats.Html) is string html
                && TryParseHtml(html, out table))
            {
                source = "Tabela reconhecida do Word, com proporção das colunas preservada.";
                return true;
            }

            if (Clipboard.ContainsText() && TryParseTabSeparated(Clipboard.GetText(), out table))
            {
                source = "Tabela reconhecida pelo conteúdo de linhas e colunas.";
                return true;
            }
        }
        catch { }
        return false;
    }

    public static bool TryParseHtml(string? clipboardHtml, out BulletinTableData table)
    {
        table = BulletinTableCodec.Create();
        if (string.IsNullOrWhiteSpace(clipboardHtml)) return false;
        var candidates = new List<BulletinTableData>();
        foreach (Match tableMatch in TableRegex.Matches(clipboardHtml))
        {
            var rows = new List<List<string>>();
            var widths = new List<double>();
            var headerDetected = false;
            foreach (Match rowMatch in RowRegex.Matches(tableMatch.Value))
            {
                var row = new List<string>();
                var rowWidths = new List<double>();
                foreach (Match cellMatch in CellRegex.Matches(rowMatch.Groups["body"].Value))
                {
                    var attrs = cellMatch.Groups["attrs"].Value;
                    var body = cellMatch.Groups["body"].Value;
                    var span = ParseSpan(attrs);
                    row.Add(HtmlToText(body));
                    rowWidths.Add(ParseWidth(attrs));
                    for (var extra = 1; extra < span; extra++)
                    {
                        row.Add(string.Empty);
                        rowWidths.Add(0);
                    }
                    if (rows.Count == 0 && (cellMatch.Groups["kind"].Value.Equals("h", StringComparison.OrdinalIgnoreCase)
                                            || Regex.IsMatch(body, @"<(?:b|strong)\b|font-weight\s*:\s*(?:bold|[6-9]00)", RegexOptions.IgnoreCase)))
                        headerDetected = true;
                }
                if (row.Count == 0) continue;
                rows.Add(row);
                if (widths.Count == 0 && rowWidths.Any(value => value > 0)) widths = rowWidths;
            }
            if (rows.Count == 0) continue;
            var columns = rows.Max(row => row.Count);
            if (columns == 0) continue;
            var parsed = new BulletinTableData
            {
                Rows = rows.Count,
                Columns = columns,
                WidthPercent = ParseTableWidth(tableMatch.Value),
                HasHeader = headerDetected || rows.Count > 1,
                Cells = rows,
                ColumnWidths = NormalizeWidths(widths, columns)
            };
            candidates.Add(BulletinTableCodec.Normalize(parsed));
        }
        if (candidates.Count == 0) return false;
        table = candidates.OrderByDescending(item => item.Rows * item.Columns).ThenByDescending(item => item.Rows).First();
        return true;
    }

    public static bool TryParseTabSeparated(string? text, out BulletinTableData table)
    {
        table = BulletinTableCodec.Create();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Trim().Split('\n', StringSplitOptions.None);
        var rows = lines.Select(line => line.Split('\t').Select(cell => cell.Trim()).ToList()).ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        if (rows.Count == 0 || columns < 2) return false;
        table = BulletinTableCodec.Normalize(new BulletinTableData
        {
            Rows = rows.Count,
            Columns = columns,
            WidthPercent = 100,
            HasHeader = rows.Count > 1,
            Cells = rows,
            ColumnWidths = Enumerable.Repeat(1, columns).ToList()
        });
        return true;
    }

    private static string HtmlToText(string html)
    {
        var value = Regex.Replace(html, @"<(?:br|hr)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"</(?:p|div|li)\s*>", "\n", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"<[^>]+>", string.Empty, RegexOptions.Singleline);
        value = WebUtility.HtmlDecode(value).Replace('\u00A0', ' ');
        value = Regex.Replace(value, @"[ \t]+", " ");
        value = Regex.Replace(value, @"\s*\n\s*", "\n");
        return value.Trim();
    }

    private static int ParseSpan(string attrs)
    {
        var match = Regex.Match(attrs, "colspan\\s*=\\s*['\\\"]?(?<value>\\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["value"].Value, out var value) ? Math.Clamp(value, 1, 12) : 1;
    }

    private static double ParseWidth(string attrs)
    {
        var match = Regex.Match(attrs, "(?:^|\\s)width\\s*=\\s*['\\\"]?(?<value>\\d+(?:[.,]\\d+)?)", RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(attrs, @"(?:^|;)\s*width\s*:\s*(?<value>\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);
        return match.Success && double.TryParse(match.Groups["value"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static int ParseTableWidth(string html)
    {
        var opening = Regex.Match(html, @"<table\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase).Groups["attrs"].Value;
        var percent = Regex.Match(opening, "(?:width\\s*=|width\\s*:)\\s*['\\\"]?(?<value>\\d{1,3})\\s*%", RegexOptions.IgnoreCase);
        return percent.Success && int.TryParse(percent.Groups["value"].Value, out var value) ? Math.Clamp(value, 30, 100) : 100;
    }

    private static List<int> NormalizeWidths(IReadOnlyList<double> widths, int columns)
    {
        var values = Enumerable.Range(0, columns).Select(index => index < widths.Count ? Math.Max(0, widths[index]) : 0).ToList();
        if (values.All(value => value <= 0)) return Enumerable.Repeat(1, columns).ToList();
        var fallback = values.Where(value => value > 0).DefaultIfEmpty(1).Average();
        return values.Select(value => Math.Max(1, (int)Math.Round(value > 0 ? value : fallback))).ToList();
    }
}
