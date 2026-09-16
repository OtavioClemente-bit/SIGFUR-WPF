using System.Text;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class BulletinTableCodec
{
    private const string MarkerPrefix = "\uE000SIGFUR_TABLE:";
    private const string MarkerSuffix = "\uE001";
    private static readonly Regex MarkerRegex = new(
        Regex.Escape(MarkerPrefix) + "(?<data>[A-Za-z0-9_-]+)" + Regex.Escape(MarkerSuffix),
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static BulletinTableData Create(int rows = 3, int columns = 3, int widthPercent = 100, bool hasHeader = true)
    {
        var table = new BulletinTableData
        {
            Rows = rows,
            Columns = columns,
            WidthPercent = widthPercent,
            HasHeader = hasHeader
        };
        return Normalize(table);
    }

    public static BulletinTableData ParseOrCreate(
        string? value,
        int rows = 3,
        int columns = 3,
        int widthPercent = 100,
        bool hasHeader = true)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<BulletinTableData>(value);
                if (parsed is not null) return Normalize(parsed);
            }
            catch { }
        }
        return Create(rows, columns, widthPercent, hasHeader);
    }

    public static string Serialize(BulletinTableData table)
        => JsonSerializer.Serialize(Normalize(table));

    public static string EncodeMarker(string? serializedTable)
    {
        var table = ParseOrCreate(serializedTable);
        return MarkerPrefix + EncodeDefinition(Serialize(table)) + MarkerSuffix;
    }

    public static string EncodeDefinition(string? serializedTable)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(Serialize(ParseOrCreate(serializedTable))))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecodeDefinition(string? base64Url, out BulletinTableData table)
        => TryDecode(base64Url ?? string.Empty, out table);

    public static bool ContainsTable(string? value)
        => !string.IsNullOrEmpty(value) && MarkerRegex.IsMatch(value);

    public static string ToPlainText(string? structuredText)
    {
        if (string.IsNullOrEmpty(structuredText)) return structuredText ?? string.Empty;
        return MarkerRegex.Replace(structuredText, match =>
            TryDecode(match.Groups["data"].Value, out var table) ? ToTabSeparatedText(table) : string.Empty);
    }

    public static IReadOnlyList<BulletinContentSegment> Split(string? structuredText)
    {
        var source = structuredText ?? string.Empty;
        var result = new List<BulletinContentSegment>();
        var cursor = 0;
        foreach (Match match in MarkerRegex.Matches(source))
        {
            if (match.Index > cursor)
                result.Add(BulletinContentSegment.ForText(source[cursor..match.Index], cursor));
            if (TryDecode(match.Groups["data"].Value, out var table))
                result.Add(BulletinContentSegment.ForTable(table, match.Index, match.Length));
            cursor = match.Index + match.Length;
        }
        if (cursor < source.Length) result.Add(BulletinContentSegment.ForText(source[cursor..], cursor));
        if (result.Count == 0) result.Add(BulletinContentSegment.ForText(source, 0));
        return result;
    }

    public static string ToTabSeparatedText(BulletinTableData table)
    {
        table = Normalize(table);
        return string.Join(Environment.NewLine,
            table.Cells.Take(table.Rows).Select(row =>
                string.Join('\t', row.Take(table.Columns).Select(CleanPlainCell))));
    }

    public static string ToSisbolHtml(BulletinTableData table)
    {
        table = Normalize(table);
        var html = new StringBuilder();
        html.Append("<div style=\"clear:both; width:100%;\">")
            .Append("<table border=\"1\" rules=\"all\" frame=\"box\" bordercolor=\"#000000\" cellspacing=\"0\" cellpadding=\"3\" width=\"")
            .Append(table.WidthPercent)
            .Append("%\" style=\"border-collapse:collapse; border-spacing:0; clear:both; float:none; position:static; table-layout:fixed; margin:6px 0; width:")
            .Append(table.WidthPercent)
            .Append("%; page-break-inside:avoid;\">");

        for (var rowIndex = 0; rowIndex < table.Rows; rowIndex++)
        {
            html.Append("<tr>");
            for (var columnIndex = 0; columnIndex < table.Columns; columnIndex++)
            {
                var width = table.ColumnWidths[columnIndex];
                html.Append("<td width=\"").Append(width).Append("%\" valign=\"middle\" style=\"border:1px solid #000000; padding:3px 5px; vertical-align:middle; width:")
                    .Append(width).Append("%; text-align:")
                    .Append(columnIndex == 0 ? "left" : "center")
                    .Append("; font-weight:")
                    .Append(table.HasHeader && rowIndex == 0 ? "bold" : "normal")
                    .Append(";\">");
                AppendEncodedCell(html, table.Cells[rowIndex][columnIndex]);
                html.Append("</td>");
            }
            html.Append("</tr>");
        }

        return html.Append("</table></div><div style=\"clear:both; height:0; line-height:0;\">&nbsp;</div>").ToString();
    }

    public static BulletinTableData Normalize(BulletinTableData table)
    {
        table.Rows = Math.Clamp(table.Rows, 1, 30);
        table.Columns = Math.Clamp(table.Columns, 1, 12);
        table.WidthPercent = Math.Clamp(table.WidthPercent, 30, 100);

        table.Cells ??= [];
        while (table.Cells.Count < table.Rows) table.Cells.Add([]);
        if (table.Cells.Count > table.Rows) table.Cells.RemoveRange(table.Rows, table.Cells.Count - table.Rows);
        foreach (var row in table.Cells)
        {
            while (row.Count < table.Columns) row.Add(string.Empty);
            if (row.Count > table.Columns) row.RemoveRange(table.Columns, row.Count - table.Columns);
        }

        table.ColumnWidths ??= [];
        while (table.ColumnWidths.Count < table.Columns) table.ColumnWidths.Add(1);
        if (table.ColumnWidths.Count > table.Columns)
            table.ColumnWidths.RemoveRange(table.Columns, table.ColumnWidths.Count - table.Columns);
        var total = table.ColumnWidths.Sum(value => Math.Max(1, value));
        var distributed = new List<int>(table.Columns);
        var used = 0;
        for (var index = 0; index < table.Columns; index++)
        {
            var width = index == table.Columns - 1
                ? 100 - used
                : Math.Max(1, (int)Math.Round(Math.Max(1, table.ColumnWidths[index]) * 100d / total));
            distributed.Add(width);
            used += width;
        }
        if (distributed[^1] < 1)
        {
            var deficit = 1 - distributed[^1];
            distributed[^1] = 1;
            distributed[0] = Math.Max(1, distributed[0] - deficit);
        }
        table.ColumnWidths = distributed;
        return table;
    }

    private static bool TryDecode(string base64Url, out BulletinTableData table)
    {
        table = Create();
        try
        {
            var value = base64Url.Replace('-', '+').Replace('_', '/');
            value += new string('=', (4 - value.Length % 4) % 4);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            var parsed = JsonSerializer.Deserialize<BulletinTableData>(json);
            if (parsed is null) return false;
            table = Normalize(parsed);
            return true;
        }
        catch { return false; }
    }

    private static string CleanPlainCell(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s*\r?\n\s*", " ").Trim();

    private static void AppendEncodedCell(StringBuilder html, string? value)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(value ?? string.Empty)
            .Replace("\r\n", "<br />", StringComparison.Ordinal)
            .Replace("\r", "<br />", StringComparison.Ordinal)
            .Replace("\n", "<br />", StringComparison.Ordinal);
        html.Append(string.IsNullOrEmpty(encoded) ? "&nbsp;" : encoded);
    }
}

public sealed record BulletinContentSegment(string Text, BulletinTableData? Table, int SourceStart, int SourceLength)
{
    public bool IsTable => Table is not null;
    public static BulletinContentSegment ForText(string text, int sourceStart) => new(text, null, sourceStart, text.Length);
    public static BulletinContentSegment ForTable(BulletinTableData table, int sourceStart, int sourceLength) => new(string.Empty, table, sourceStart, sourceLength);
}
