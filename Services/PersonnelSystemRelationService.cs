using System.Security;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Gera a relação SIAPPES/SIPPES em ODT com a identidade da OM ativa.
/// O documento é editável no LibreOffice/Word e mantém as seções de Oficiais e Praças.
/// </summary>
public static class PersonnelSystemRelationService
{
    private const string Sippes = "SIPPES";
    private const string Siappes = "SIAPPES";

    public static async Task ExportOdtAsync(
        string path,
        IReadOnlyList<PersonnelSystemRelationItem> items,
        DateTime generatedAt,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0) throw new InvalidOperationException("Não há militares para gerar a relação.");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temp))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                var mime = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
                await using (var writer = new StreamWriter(mime.Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync("application/vnd.oasis.opendocument.text".AsMemory(), cancellationToken);

                await WriteEntryAsync(archive, "content.xml", BuildContent(items, generatedAt), cancellationToken);
                await WriteEntryAsync(archive, "styles.xml", StylesXml, cancellationToken);
                await WriteEntryAsync(archive, "meta.xml", BuildMeta(generatedAt), cancellationToken);
                await WriteEntryAsync(archive, "settings.xml", SettingsXml, cancellationToken);
                await WriteEntryAsync(archive, "META-INF/manifest.xml", ManifestXml, cancellationToken);
            }
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static string NormalizeSystem(string? value)
        => string.Equals((value ?? string.Empty).Trim(), Siappes, StringComparison.OrdinalIgnoreCase) ? Siappes : Sippes;

    private static string BuildContent(IReadOnlyList<PersonnelSystemRelationItem> items, DateTime generatedAt)
    {
        var ordered = items
            .Select((item, index) => new { Item = item, Index = index })
            .OrderBy(x => x.Index)
            .ToList();
        var sb = new StringBuilder(64_000);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" office:version=\"1.2\">");
        sb.Append("<office:automatic-styles>");
        sb.Append("<style:style style:name=\"Title\" style:family=\"paragraph\"><style:paragraph-properties fo:text-align=\"center\" fo:margin-top=\"0.25cm\" fo:margin-bottom=\"0.55cm\"/><style:text-properties fo:font-family=\"Times New Roman\" fo:font-size=\"16pt\" fo:font-weight=\"bold\" style:text-underline-style=\"solid\" style:text-underline-width=\"auto\"/></style:style>");
        sb.Append("<style:style style:name=\"Spacer\" style:family=\"paragraph\"><style:paragraph-properties fo:margin-top=\"0.15cm\" fo:margin-bottom=\"0.15cm\"/></style:style>");
        sb.Append("<style:style style:name=\"Table\" style:family=\"table\"><style:table-properties style:width=\"26.5cm\" table:align=\"center\"/></style:style>");
        var widths = new[] { "2.0cm", "2.1cm", "9.4cm", "4.0cm", "3.6cm", "3.1cm", "2.3cm" };
        for (var i = 0; i < widths.Length; i++)
            sb.Append($"<style:style style:name=\"Col{i + 1}\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"{widths[i]}\"/></style:style>");
        sb.Append("<style:style style:name=\"Cell\" style:family=\"table-cell\"><style:table-cell-properties fo:padding=\"0.07cm\" fo:border=\"0.02cm solid #000000\" style:vertical-align=\"middle\"/></style:style>");
        sb.Append("<style:style style:name=\"PData\" style:family=\"paragraph\"><style:paragraph-properties fo:text-align=\"center\" fo:margin=\"0cm\"/><style:text-properties fo:font-family=\"Times New Roman\" fo:font-size=\"10.5pt\"/></style:style>");
        sb.Append("<style:style style:name=\"PHeader\" style:family=\"paragraph\"><style:paragraph-properties fo:text-align=\"center\" fo:margin=\"0cm\"/><style:text-properties fo:font-family=\"Times New Roman\" fo:font-size=\"12pt\" fo:font-weight=\"bold\"/></style:style>");
        sb.Append("<style:style style:name=\"PGroup\" style:family=\"paragraph\"><style:paragraph-properties fo:text-align=\"center\" fo:margin=\"0cm\"/><style:text-properties fo:font-family=\"Times New Roman\" fo:font-size=\"15pt\" fo:font-weight=\"bold\"/></style:style>");
        sb.Append("<style:style style:name=\"Bold\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\"/></style:style>");
        sb.Append("</office:automatic-styles><office:body><office:text>");

        AppendSystemSection(sb, Siappes, ordered.Where(x => NormalizeSystem(x.Item.PaymentSystem) == Siappes).Select(x => x.Item.Military).ToList(), generatedAt);
        sb.Append("<text:p text:style-name=\"Spacer\"/>");
        AppendSystemSection(sb, Sippes, ordered.Where(x => NormalizeSystem(x.Item.PaymentSystem) == Sippes).Select(x => x.Item.Military).ToList(), generatedAt);

        sb.Append("</office:text></office:body></office:document-content>");
        return sb.ToString();
    }

    private static void AppendSystemSection(StringBuilder sb, string system, IReadOnlyList<MilitaryRecord> rows, DateTime generatedAt)
    {
        var date = generatedAt.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("pt-BR"))
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        AppendParagraph(sb, "Title", $"RELAÇÃO DE MILITARES {system} - {OrganizationIdentity.UpperName} - {date}");
        sb.Append($"<table:table table:name=\"Relacao{system}\" table:style-name=\"Table\">");
        for (var i = 1; i <= 7; i++) sb.Append($"<table:table-column table:style-name=\"Col{i}\"/>");

        var officers = rows.Where(IsOfficer).ToList();
        var enlisted = rows.Where(x => !IsOfficer(x)).ToList();
        AppendGroupRow(sb, "OFICIAIS");
        AppendHeaderRow(sb, system == Siappes ? "ANTIG" : "ORD", system == Siappes ? "DATA PRAÇA" : "ANO");
        var order = 1;
        foreach (var row in officers) AppendMilitaryRow(sb, row, order++);
        AppendGroupRow(sb, "PRAÇAS");
        foreach (var row in enlisted) AppendMilitaryRow(sb, row, order++);
        if (rows.Count == 0) AppendEmptyRow(sb);
        sb.Append("</table:table>");
    }

    private static void AppendGroupRow(StringBuilder sb, string text)
    {
        sb.Append("<table:table-row><table:table-cell table:style-name=\"Cell\" table:number-columns-spanned=\"7\" office:value-type=\"string\">");
        AppendParagraph(sb, "PGroup", text);
        sb.Append("</table:table-cell>");
        for (var i = 0; i < 6; i++) sb.Append("<table:covered-table-cell/>");
        sb.Append("</table:table-row>");
    }

    private static void AppendHeaderRow(StringBuilder sb, string orderHeader, string yearHeader)
    {
        sb.Append("<table:table-row>");
        foreach (var value in new[] { orderHeader, "P/G", "NOME", "CPF", "PREC-CP", "IDT", yearHeader }) AppendCell(sb, value, "PHeader");
        sb.Append("</table:table-row>");
    }

    private static void AppendMilitaryRow(StringBuilder sb, MilitaryRecord row, int order)
    {
        sb.Append("<table:table-row>");
        AppendCell(sb, order.ToString("00", CultureInfo.InvariantCulture), "PData");
        AppendCell(sb, RelationRank(row.Rank), "PData");
        sb.Append("<table:table-cell table:style-name=\"Cell\" office:value-type=\"string\"><text:p text:style-name=\"PData\">");
        foreach (var segment in NameHighlightHelper.BuildSegments((row.Name ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR")), (row.WarName ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR"))))
        {
            if (segment.IsBold) sb.Append("<text:span text:style-name=\"Bold\">");
            sb.Append(Xml(segment.Text));
            if (segment.IsBold) sb.Append("</text:span>");
        }
        sb.Append("</text:p></table:table-cell>");
        AppendCell(sb, MilitaryFormatting.FormatCpf(row.Cpf), "PData");
        AppendCell(sb, FormatPrec(row.PrecCp), "PData");
        AppendCell(sb, FormatIdentity(row.MilitaryId), "PData");
        AppendCell(sb, FormationYear(row), "PData");
        sb.Append("</table:table-row>");
    }

    private static void AppendEmptyRow(StringBuilder sb)
    {
        sb.Append("<table:table-row><table:table-cell table:style-name=\"Cell\" table:number-columns-spanned=\"7\" office:value-type=\"string\">");
        AppendParagraph(sb, "PData", "SEM MILITARES CLASSIFICADOS");
        sb.Append("</table:table-cell>");
        for (var i = 0; i < 6; i++) sb.Append("<table:covered-table-cell/>");
        sb.Append("</table:table-row>");
    }

    private static void AppendCell(StringBuilder sb, string? value, string paragraphStyle)
    {
        sb.Append("<table:table-cell table:style-name=\"Cell\" office:value-type=\"string\">");
        AppendParagraph(sb, paragraphStyle, value);
        sb.Append("</table:table-cell>");
    }

    private static void AppendParagraph(StringBuilder sb, string style, string? value)
        => sb.Append($"<text:p text:style-name=\"{style}\">{Xml(value)}</text:p>");

    private static bool IsOfficer(MilitaryRecord row) => MilitaryRankService.GetOrder(row.Rank) <= 10;

    private static string RelationRank(string? rank)
        => MilitaryRankService.Canonicalize(rank) switch
        {
            "General de Exército" => "GEN EX", "General de Divisão" => "GEN DIV", "General de Brigada" => "GEN BDA",
            "Coronel" => "CEL", "Tenente Coronel" => "TEN CEL", "Major" => "MAJ", "Capitão" => "CAP",
            "1º Tenente" => "1º TEN", "2º Tenente" => "2º TEN", "Tenente" => "TEN", "Aspirante" => "ASP",
            "Subtenente" => "ST", "1º Sargento" => "1º SGT", "2º Sargento" => "2º SGT", "3º Sargento" => "3º SGT",
            "Cabo Efetivo Profissional" => "CB EP", "Soldado Efetivo Profissional" => "SD EP", "Soldado Efetivo Variável" => "SD EV",
            var other => string.IsNullOrWhiteSpace(other) ? "—" : other.ToUpper(CultureInfo.GetCultureInfo("pt-BR"))
        };

    private static string FormationYear(MilitaryRecord row)
    {
        var year = new string((row.FormationYear ?? string.Empty).Where(char.IsDigit).ToArray());
        if (year.Length >= 4) return year[..4];
        var date = MilitaryFormatting.ParseDate(row.EnlistmentDate);
        return date?.Year.ToString(CultureInfo.InvariantCulture) ?? year;
    }

    private static string FormatPrec(string? value)
    {
        var digits = Digits(value);
        return digits.Length == 9 ? $"{digits[..8]}-{digits[8]}" : digits;
    }

    private static string FormatIdentity(string? value)
    {
        var digits = Digits(value);
        if (digits.Length == 10) return $"{digits[..3]}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits[9]}";
        if (digits.Length == 9) return $"{digits[..2]}.{digits.Substring(2, 3)}.{digits.Substring(5, 3)}-{digits[8]}";
        return digits;
    }

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string Xml(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string text, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private static string BuildMeta(DateTime generatedAt) => $"""
<?xml version="1.0" encoding="UTF-8"?>
<office:document-meta xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:meta="urn:oasis:names:tc:opendocument:xmlns:meta:1.0" xmlns:dc="http://purl.org/dc/elements/1.1/" office:version="1.2"><office:meta><dc:title>Relação de Militares SIAPPES e SIPPES</dc:title><meta:generator>SIGFUR</meta:generator><meta:creation-date>{generatedAt:yyyy-MM-ddTHH:mm:ss}</meta:creation-date></office:meta></office:document-meta>
""";

    private const string SettingsXml = """
<?xml version="1.0" encoding="UTF-8"?>
<office:document-settings xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" office:version="1.2"><office:settings/></office:document-settings>
""";

    private const string StylesXml = """
<?xml version="1.0" encoding="UTF-8"?>
<office:document-styles xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" office:version="1.2">
  <office:styles><style:default-style style:family="paragraph"><style:text-properties fo:font-family="Times New Roman" fo:font-size="10.5pt"/></style:default-style></office:styles>
  <office:automatic-styles><style:page-layout style:name="Landscape"><style:page-layout-properties fo:page-width="29.7cm" fo:page-height="21cm" style:print-orientation="landscape" fo:margin-top="1.0cm" fo:margin-bottom="1.0cm" fo:margin-left="1.6cm" fo:margin-right="1.6cm"/></style:page-layout></office:automatic-styles>
  <office:master-styles><style:master-page style:name="Standard" style:page-layout-name="Landscape"/></office:master-styles>
</office:document-styles>
""";

    private const string ManifestXml = """
<?xml version="1.0" encoding="UTF-8"?>
<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2"><manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="styles.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="meta.xml" manifest:media-type="text/xml"/><manifest:file-entry manifest:full-path="settings.xml" manifest:media-type="text/xml"/></manifest:manifest>
""";
}
