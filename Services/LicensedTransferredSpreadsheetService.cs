using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed record LicensedExportColumn(
    string Key,
    string Header,
    double Width,
    bool DefaultSelected,
    Func<LicensedTransferredRecord, string> Value);

public sealed class LicensedTransferredSpreadsheetService
{
    public static IReadOnlyList<LicensedExportColumn> ExportColumns { get; } =
    [
        new("id", "ID", 9, false, r => r.Id.ToString(CultureInfo.InvariantCulture)),
        new("rank", "Posto/Graduação", 22, true, r => r.ShortRank),
        new("name", "Nome completo", 44, true, r => r.Name),
        new("precCp", "PREC-CP", 17, true, r => r.FormattedPrecCp),
        new("cpf", "CPF", 17, true, r => r.FormattedCpf),
        new("militaryId", "IDT", 18, true, r => r.MilitaryId),
        new("reason", "Motivo", 25, true, r => r.Reason),
        new("destination", "Destino / OM", 28, true, r => r.Destination),
        new("formationYear", "Ano de Formação", 16, true, r => r.FormationYear),
        new("birthDate", "Data de Nascimento", 18, true, r => r.BirthDate),
        new("enlistmentDate", "Data de Praça", 16, true, r => r.EnlistmentDate),
        new("phone", "Telefone", 19, true, r => r.Phone),
        new("email", "E-mail", 34, true, r => r.Email),
        new("education", "Escolaridade", 24, true, r => r.Education),
        new("address", "Endereço", 48, true, r => r.Address),
        new("zipCode", "CEP", 14, true, r => r.ZipCode),
        new("bank", "Banco", 22, true, r => r.Bank),
        new("agency", "Agência", 15, true, r => r.Agency),
        new("account", "Conta", 20, true, r => r.Account),
        new("photoPath", "Caminho da Foto", 48, false, r => r.PhotoPath),
        new("receivesPreSchool", "Recebe Pré-Escolar", 20, false, r => r.ReceivesPreSchool),
        new("preSchoolValue", "Valor Pré-Escolar", 20, false, r => r.PreSchoolValue),
        new("receivesTransportAid", "Recebe Auxílio-Transporte", 25, true, r => r.ReceivesTransportAid),
        new("transportAidValue", "Valor Auxílio-Transporte", 25, true, r => r.TransportAidValue),
        new("transportGrossTotal", "AT Bruto Total", 19, false, r => r.TransportGrossTotal?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty),
        new("transportWorkingDays", "Dias Úteis AT", 16, false, r => r.TransportWorkingDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
        new("transportBaseTimestamp", "Atualização da Base AT", 23, false, r => r.TransportBaseTimestamp),
        new("hasPnr", "Possui PNR", 15, false, r => r.HasPnr),
        new("alimony", "Pensão Alimentícia", 20, false, r => r.Alimony),
        new("alimonyValue", "Valor da Pensão", 19, false, r => r.AlimonyValue),
        new("visible", "Status", 14, true, r => r.StatusText)
    ];

    public Task ExportCsvAsync(string path, IEnumerable<LicensedTransferredRecord> records, CancellationToken cancellationToken = default)
        => ExportCsvAsync(path, records, ExportColumns.Where(x => x.DefaultSelected).Select(x => x.Key), cancellationToken);

    public async Task ExportCsvAsync(
        string path,
        IEnumerable<LicensedTransferredRecord> records,
        IEnumerable<string> selectedColumnKeys,
        CancellationToken cancellationToken = default)
    {
        var columns = ResolveColumns(selectedColumnKeys);
        var lines = new List<string> { string.Join(";", columns.Select(x => Csv(x.Header))) };
        lines.AddRange(records.Select(r => string.Join(";", columns.Select(x => Csv(x.Value(r))))));
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true), cancellationToken);
    }

    public Task ExportXlsxAsync(string path, IEnumerable<LicensedTransferredRecord> records, CancellationToken cancellationToken = default)
        => ExportXlsxAsync(path, records, ExportColumns.Where(x => x.DefaultSelected).Select(x => x.Key), cancellationToken);

    public async Task ExportXlsxAsync(
        string path,
        IEnumerable<LicensedTransferredRecord> records,
        IEnumerable<string> selectedColumnKeys,
        CancellationToken cancellationToken = default)
    {
        var columns = ResolveColumns(selectedColumnKeys);
        var rows = records.ToList();
        await Task.Run(() => WriteProfessionalXlsx(path, columns, rows), cancellationToken);
    }

    public async Task<List<LicensedTransferredRecord>> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Arquivo não encontrado.", path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var table = extension == ".xlsx"
            ? await Task.Run(() => ReadXlsx(path), cancellationToken)
            : await ReadDelimitedAsync(path, cancellationToken);
        return MapRows(table);
    }

    private static IReadOnlyList<LicensedExportColumn> ResolveColumns(IEnumerable<string> selectedColumnKeys)
    {
        var selected = new HashSet<string>(selectedColumnKeys ?? [], StringComparer.OrdinalIgnoreCase);
        var columns = ExportColumns.Where(x => selected.Contains(x.Key)).ToList();
        if (columns.Count == 0) throw new InvalidOperationException("Selecione ao menos uma coluna para exportar.");
        return columns;
    }

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? '"' + text.Replace("\"", "\"\"") + '"' : text;
    }

    private static async Task<List<string[]>> ReadDelimitedAsync(string path, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Length == 0) return [];
        var delimiter = DetectDelimiter(lines[0]);
        return lines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => ParseDelimitedLine(x, delimiter)).ToList();
    }

    private static char DetectDelimiter(string line)
    {
        var candidates = new[] { ';', '\t', ',' };
        return candidates.OrderByDescending(x => CountOutsideQuotes(line, x)).First();
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') i++;
                else quoted = !quoted;
            }
            else if (!quoted && line[i] == delimiter) count++;
        }
        return count;
    }

    private static string[] ParseDelimitedLine(string line, char delimiter)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (!quoted && ch == delimiter) { result.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(ch);
        }
        result.Add(current.ToString().Trim());
        return result.ToArray();
    }

    private static void WriteProfessionalXlsx(
        string path,
        IReadOnlyList<LicensedExportColumn> columns,
        IReadOnlyList<LicensedTransferredRecord> records)
    {
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Licenciados e Transferidos" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="3">
                <font><sz val="11"/><name val="Calibri"/><family val="2"/></font>
                <font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/><family val="2"/></font>
                <font><b/><sz val="11"/><name val="Calibri"/><family val="2"/></font>
              </fonts>
              <fills count="4">
                <fill><patternFill patternType="none"/></fill>
                <fill><patternFill patternType="gray125"/></fill>
                <fill><patternFill patternType="solid"><fgColor rgb="FF0D47A1"/><bgColor indexed="64"/></patternFill></fill>
                <fill><patternFill patternType="solid"><fgColor rgb="FFF2F7FC"/><bgColor indexed="64"/></patternFill></fill>
              </fills>
              <borders count="2">
                <border><left/><right/><top/><bottom/><diagonal/></border>
                <border><left style="thin"><color rgb="FFD9E2EC"/></left><right style="thin"><color rgb="FFD9E2EC"/></right><top style="thin"><color rgb="FFD9E2EC"/></top><bottom style="thin"><color rgb="FFD9E2EC"/></bottom><diagonal/></border>
              </borders>
              <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
              <cellXfs count="4">
                <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0"><alignment vertical="center" wrapText="1"/></xf>
                <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
                <xf numFmtId="0" fontId="0" fillId="3" borderId="1" xfId="0" applyFill="1"><alignment vertical="center" wrapText="1"/></xf>
                <xf numFmtId="0" fontId="2" fillId="0" borderId="1" xfId="0" applyFont="1"><alignment vertical="center" wrapText="1"/></xf>
              </cellXfs>
            </styleSheet>
            """);

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var sheetData = new XElement(ns + "sheetData");
        var header = new XElement(ns + "row", new XAttribute("r", 1), new XAttribute("ht", 26), new XAttribute("customHeight", 1));
        for (var col = 0; col < columns.Count; col++)
            header.Add(InlineCell(ns, ColumnName(col + 1) + "1", columns[col].Header, 1));
        sheetData.Add(header);

        for (var rowIndex = 0; rowIndex < records.Count; rowIndex++)
        {
            var record = records[rowIndex];
            var excelRow = rowIndex + 2;
            var row = new XElement(ns + "row", new XAttribute("r", excelRow), new XAttribute("ht", 22), new XAttribute("customHeight", 1));
            for (var col = 0; col < columns.Count; col++)
            {
                var column = columns[col];
                var style = rowIndex % 2 == 1 ? 2 : 0;
                var reference = ColumnName(col + 1) + excelRow;
                row.Add(column.Key.Equals("name", StringComparison.OrdinalIgnoreCase)
                    ? RichNameCell(ns, reference, record, style)
                    : InlineCell(ns, reference, column.Value(record), style));
            }
            sheetData.Add(row);
        }

        var widths = new XElement(ns + "cols", columns.Select((column, index) => new XElement(ns + "col",
            new XAttribute("min", index + 1), new XAttribute("max", index + 1),
            new XAttribute("width", column.Width), new XAttribute("customWidth", 1))));
        var lastColumn = ColumnName(columns.Count);
        var lastRow = Math.Max(1, records.Count + 1);
        var worksheet = new XElement(ns + "worksheet",
            new XElement(ns + "sheetViews",
                new XElement(ns + "sheetView", new XAttribute("workbookViewId", 0),
                    new XElement(ns + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
            new XElement(ns + "sheetFormatPr", new XAttribute("defaultRowHeight", 18)),
            widths,
            sheetData,
            new XElement(ns + "autoFilter", new XAttribute("ref", $"A1:{lastColumn}{lastRow}")),
            new XElement(ns + "pageMargins", new XAttribute("left", 0.25), new XAttribute("right", 0.25), new XAttribute("top", 0.5), new XAttribute("bottom", 0.5), new XAttribute("header", 0.2), new XAttribute("footer", 0.2)));
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), worksheet);
        WriteEntry(archive, "xl/worksheets/sheet1.xml", document.ToString(SaveOptions.DisableFormatting));
    }

    private static XElement InlineCell(XNamespace ns, string reference, string? text, int style)
        => new(ns + "c",
            new XAttribute("r", reference), new XAttribute("t", "inlineStr"), new XAttribute("s", style),
            new XElement(ns + "is", new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), SanitizeXml(text))));

    private static XElement RichNameCell(XNamespace ns, string reference, LicensedTransferredRecord record, int style)
    {
        var (before, war, after) = SplitWarName(record.Name, record.WarName);
        if (string.IsNullOrWhiteSpace(war)) return InlineCell(ns, reference, record.Name, style);
        var inline = new XElement(ns + "is");
        if (!string.IsNullOrEmpty(before)) inline.Add(Run(ns, before, false));
        inline.Add(Run(ns, war, true));
        if (!string.IsNullOrEmpty(after)) inline.Add(Run(ns, after, false));
        return new XElement(ns + "c", new XAttribute("r", reference), new XAttribute("t", "inlineStr"), new XAttribute("s", style), inline);
    }

    private static XElement Run(XNamespace ns, string text, bool bold)
    {
        var run = new XElement(ns + "r");
        if (bold) run.Add(new XElement(ns + "rPr", new XElement(ns + "b"), new XElement(ns + "rFont", new XAttribute("val", "Calibri")), new XElement(ns + "sz", new XAttribute("val", 11))));
        run.Add(new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), SanitizeXml(text)));
        return run;
    }

    private static (string Before, string War, string After) SplitWarName(string? fullName, string? warName)
    {
        var full = (fullName ?? string.Empty).Trim();
        var war = (warName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(full) || string.IsNullOrEmpty(war)) return (full, string.Empty, string.Empty);
        var index = CultureInfo.GetCultureInfo("pt-BR").CompareInfo.IndexOf(full, war, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        return index < 0 ? (full + " — ", war, string.Empty) : (full[..index], full.Substring(index, war.Length), full[(index + war.Length)..]);
    }

    private static List<string[]> ReadXlsx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var shared = ReadSharedStrings(archive);
        var sheetPath = ResolveFirstSheetPath(archive);
        var entry = archive.GetEntry(sheetPath) ?? throw new InvalidDataException("A primeira planilha do arquivo não foi localizada.");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var result = new List<string[]>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var cells = new SortedDictionary<int, string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? string.Empty;
                var column = ColumnIndex(reference);
                var type = (string?)cell.Attribute("t") ?? string.Empty;
                string value;
                if (type == "inlineStr") value = string.Concat(cell.Descendants(ns + "t").Select(x => x.Value));
                else
                {
                    value = cell.Element(ns + "v")?.Value ?? string.Empty;
                    if (type == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Count) value = shared[index];
                }
                cells[column] = value.Trim();
            }
            if (cells.Count == 0) continue;
            var max = cells.Keys.Max();
            var values = Enumerable.Repeat(string.Empty, max + 1).ToArray();
            foreach (var (index, value) in cells) values[index] = value;
            result.Add(values);
        }
        return result;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return doc.Descendants(ns + "si").Select(x => string.Concat(x.Descendants(ns + "t").Select(t => t.Value))).ToList();
    }

    private static string ResolveFirstSheetPath(ZipArchive archive)
    {
        var workbook = archive.GetEntry("xl/workbook.xml");
        var rels = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbook is null || rels is null) return "xl/worksheets/sheet1.xml";
        using var wbStream = workbook.Open();
        using var relStream = rels.Open();
        var wb = XDocument.Load(wbStream);
        var rd = XDocument.Load(relStream);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace package = "http://schemas.openxmlformats.org/package/2006/relationships";
        var id = (string?)wb.Descendants(main + "sheet").FirstOrDefault()?.Attribute(rel + "id");
        var target = rd.Descendants(package + "Relationship").FirstOrDefault(x => (string?)x.Attribute("Id") == id)?.Attribute("Target")?.Value;
        if (string.IsNullOrWhiteSpace(target)) return "xl/worksheets/sheet1.xml";
        target = target.Replace('\\', '/').TrimStart('/');
        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
    }

    private static List<LicensedTransferredRecord> MapRows(IReadOnlyList<string[]> table)
    {
        if (table.Count == 0) return [];
        var headers = table[0].Select(NormalizeHeader).ToArray();
        int Find(params string[] names)
        {
            foreach (var name in names)
            {
                var index = Array.FindIndex(headers, h => h == NormalizeHeader(name));
                if (index >= 0) return index;
            }
            return -1;
        }

        var map = new
        {
            Rank = Find("p/g", "pg", "posto", "posto/graduação", "posto_grad"), Name = Find("nome", "nome completo"), War = Find("nome de guerra", "nome_guerra"),
            Prec = Find("prec-cp", "prec", "prec_cp"), Cpf = Find("cpf"), Idt = Find("idt", "idt militar"), Reason = Find("motivo"),
            Destination = Find("destino", "om", "destino/om", "destino / om"), Year = Find("ano", "ano de formação", "ano formação"), Phone = Find("telefone", "celular"),
            Email = Find("e-mail", "email"), Education = Find("escolaridade"), Bank = Find("banco"), Agency = Find("agência", "agencia"),
            Account = Find("conta"), Birth = Find("data nascimento", "data de nascimento", "nascimento"), Enlistment = Find("data praça", "data de praça", "data praca"),
            Address = Find("endereço", "endereco"), Zip = Find("cep"), Visible = Find("visível", "visivel", "status"), Photo = Find("caminho da foto", "foto"),
            PreSchool = Find("recebe pré-escolar", "recebe pre-escolar"), PreSchoolValue = Find("valor pré-escolar", "valor pre-escolar"),
            Transport = Find("recebe auxílio-transporte", "recebe auxilio-transporte"), TransportValue = Find("valor auxílio-transporte", "valor auxilio-transporte"),
            Pnr = Find("possui pnr", "pnr"), Alimony = Find("pensão alimentícia", "pensao alimenticia"), AlimonyValue = Find("valor da pensão", "valor da pensao")
        };
        if (map.Name < 0) throw new InvalidDataException("A planilha precisa ter pelo menos a coluna Nome.");
        string Get(string[] row, int index) => index >= 0 && index < row.Length ? row[index].Trim() : string.Empty;
        var result = new List<LicensedTransferredRecord>();
        foreach (var row in table.Skip(1))
        {
            var name = Get(row, map.Name);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var visibleText = Get(row, map.Visible);
            result.Add(new LicensedTransferredRecord
            {
                Rank = MilitaryRankService.Canonicalize(Get(row, map.Rank)), Name = name, WarName = Get(row, map.War), PrecCp = Get(row, map.Prec),
                Cpf = MilitaryFormatting.Digits(Get(row, map.Cpf)), MilitaryId = Get(row, map.Idt), Reason = Get(row, map.Reason), Destination = Get(row, map.Destination),
                FormationYear = Get(row, map.Year), Phone = Get(row, map.Phone), Email = Get(row, map.Email), Education = Get(row, map.Education),
                Bank = Get(row, map.Bank), Agency = Get(row, map.Agency), Account = Get(row, map.Account), BirthDate = Get(row, map.Birth),
                EnlistmentDate = Get(row, map.Enlistment), Address = Get(row, map.Address), ZipCode = Get(row, map.Zip), PhotoPath = Get(row, map.Photo),
                ReceivesPreSchool = Get(row, map.PreSchool), PreSchoolValue = Get(row, map.PreSchoolValue), ReceivesTransportAid = Get(row, map.Transport),
                TransportAidValue = Get(row, map.TransportValue), HasPnr = Get(row, map.Pnr), Alimony = Get(row, map.Alimony), AlimonyValue = Get(row, map.AlimonyValue),
                IsVisible = string.IsNullOrWhiteSpace(visibleText) || MilitaryRecord.IsYes(visibleText) || visibleText.Equals("visível", StringComparison.OrdinalIgnoreCase)
            });
        }
        return result;
    }

    private static string NormalizeHeader(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && (char.IsLetterOrDigit(c) || c is '/' or '-')).ToArray()).ToLowerInvariant();
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var ch in reference.TakeWhile(char.IsLetter)) index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        return Math.Max(0, index - 1);
    }

    private static string ColumnName(int index)
    {
        var result = string.Empty;
        while (index > 0) { index--; result = (char)('A' + index % 26) + result; index /= 26; }
        return result;
    }

    private static string SanitizeXml(string? value)
        => new((value ?? string.Empty).Where(ch => ch is '\t' or '\n' or '\r' || ch >= 0x20).ToArray());

    private static void WriteEntry(ZipArchive archive, string path, string text)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text.TrimStart());
    }
}
