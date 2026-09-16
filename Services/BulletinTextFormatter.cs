using System.Text.RegularExpressions;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class BulletinTextFormatter
{
    public const string StandardFontFamily = "Times New Roman";
    public const double StandardFontSizePt = 10.0;
    public static double StandardWpfFontSize => StandardFontSizePt * 96.0 / 72.0;
    public static List<BulletinBoldRange> FindWarNameRanges(string text, IEnumerable<MilitaryRecord> military)
        => FindWarNameRanges(text, military.Select(item => (item.Name, item.WarName)));

    public static List<BulletinBoldRange> FindWarNameRanges(
        string text,
        IEnumerable<(string FullName, string WarName)> names)
    {
        var ranges = new List<BulletinBoldRange>();
        if (string.IsNullOrEmpty(text)) return ranges;

        foreach (var (fullName, warName) in names)
        {
            var war = (warName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(war)) continue;

            var displayName = NameHighlightHelper.PlainDisplay(fullName, war);
            var segments = NameHighlightHelper.BuildSegments(fullName, war);
            var nameStart = 0;
            while (!string.IsNullOrWhiteSpace(displayName)
                   && (nameStart = text.IndexOf(displayName, nameStart, StringComparison.CurrentCultureIgnoreCase)) >= 0)
            {
                var offset = 0;
                foreach (var segment in segments)
                {
                    if (segment.IsBold && segment.Text.Length > 0)
                        ranges.Add(new BulletinBoldRange(nameStart + offset, segment.Text.Length));
                    offset += segment.Text.Length;
                }
                nameStart += Math.Max(1, displayName.Length);
            }

            // O SIGFUR só destaca o nome de guerra quando ele estiver dentro do nome completo formatado.
            // Não fazemos mais destaque por palavra solta para evitar casos como SILVA, DA, DE etc.
        }

        return MergeRanges(ranges, text.Length);
    }

    public static string BuildHtml(string text, IReadOnlyList<MilitaryRecord> military)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!BulletinTableCodec.ContainsTable(normalized)) return BuildTextHtml(normalized, military);

        // O PDF antigo do SisBol pode tratar tabelas centralizadas como elementos
        // flutuantes. A tabela do SIGFUR não usa align/float e cada bloco limpa a
        // flutuação anterior para conservar rigorosamente a ordem do editor.
        var html = new StringBuilder("<div style=\"text-align:justify; clear:both;\">");
        foreach (var segment in BulletinTableCodec.Split(normalized))
        {
            if (segment.Table is not null)
            {
                html.Append(BulletinTableCodec.ToSisbolHtml(segment.Table));
                continue;
            }
            if (segment.Text.Length == 0) continue;
            html.Append("<div style=\"display:block; clear:both; text-align:justify;\">")
                .Append(BuildTextHtmlContent(segment.Text, military))
                .Append("</div>");
        }
        return html.Append("</div>").ToString();
    }

    private static string BuildTextHtml(string normalized, IReadOnlyList<MilitaryRecord> military)
        => "<div style=\"text-align: justify;\">" + BuildTextHtmlContent(normalized, military) + "</div>";

    private static string BuildTextHtmlContent(string normalized, IReadOnlyList<MilitaryRecord> military)
    {
        var ranges = FindWarNameRanges(normalized, military);
        var html = new StringBuilder();
        var cursor = 0;

        foreach (var range in ranges)
        {
            AppendHtml(html, normalized[cursor..range.Start], false);
            AppendHtml(html, normalized.Substring(range.Start, range.Length), true);
            cursor = range.Start + range.Length;
            if (cursor >= normalized.Length || normalized[cursor] == '\n')
                html.Append("&nbsp;&nbsp;");
        }

        if (cursor < normalized.Length) AppendHtml(html, normalized[cursor..], false);
        if (normalized.Length == 0) AppendHtml(html, string.Empty, false);
        return html.ToString();
    }

    private static void AppendHtml(StringBuilder html, string text, bool bold)
    {
        var encoded = System.Net.WebUtility.HtmlEncode(text).Replace("\n", "<br />", StringComparison.Ordinal);
        if (bold) html.Append("<strong>").Append(encoded).Append("</strong>");
        else html.Append(encoded);
    }

    private static List<BulletinBoldRange> MergeRanges(IEnumerable<BulletinBoldRange> source, int textLength)
    {
        var ordered = source
            .Where(item => item.Start >= 0 && item.Length > 0 && item.Start < textLength)
            .Select(item => new BulletinBoldRange(item.Start, Math.Min(item.Length, textLength - item.Start)))
            .OrderBy(item => item.Start)
            .ThenBy(item => item.Length)
            .ToList();
        var result = new List<BulletinBoldRange>();
        foreach (var item in ordered)
        {
            if (result.Count == 0 || item.Start > result[^1].Start + result[^1].Length)
            {
                result.Add(item);
                continue;
            }

            var previous = result[^1];
            var end = Math.Max(previous.Start + previous.Length, item.Start + item.Length);
            result[^1] = new BulletinBoldRange(previous.Start, end - previous.Start);
        }
        return result;
    }
}
