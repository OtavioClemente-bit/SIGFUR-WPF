using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace SIGFUR.Wpf.Services;

public sealed class SpreadsheetRun
{
    public string Text { get; init; } = string.Empty;
    public bool Bold { get; init; }
}

public sealed class SpreadsheetCell
{
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<SpreadsheetRun>? Runs { get; init; }
}

public sealed class SpreadsheetRow
{
    public IReadOnlyList<SpreadsheetCell> Cells { get; init; } = [];
    public bool IsGroup { get; init; }
}

/// <summary>
/// Leitor/escritor OpenXML mínimo e seguro, sem depender do Excel instalado.
/// O XML é validado antes de o arquivo final substituir o destino.
/// </summary>
public static class SpreadsheetService
{
    public static async Task WriteXlsxAsync(
        string path,
        string sheetName,
        IReadOnlyList<string> headers,
        IReadOnlyList<double> widths,
        IReadOnlyList<SpreadsheetRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (headers.Count == 0) throw new InvalidOperationException("A planilha precisa ter ao menos uma coluna.");
        if (widths.Count != headers.Count) throw new InvalidOperationException("A quantidade de larguras não corresponde às colunas.");

        var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, "[Content_Types].xml", ContentTypesXml, cancellationToken);
                await WriteEntryAsync(archive, "_rels/.rels", RootRelsXml, cancellationToken);
                await WriteEntryAsync(archive, "xl/workbook.xml", BuildWorkbookXml(sheetName), cancellationToken);
                await WriteEntryAsync(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml, cancellationToken);
                await WriteEntryAsync(archive, "xl/styles.xml", StylesXml, cancellationToken);
                await WriteEntryAsync(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(headers, widths, rows), cancellationToken);
                await WriteEntryAsync(archive, "docProps/core.xml", BuildCoreXml(sheetName), cancellationToken);
                await WriteEntryAsync(archive, "docProps/app.xml", AppXml, cancellationToken);
            }

            ValidateXlsx(temp);
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static async Task<List<List<string>>> ReadTabularFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" => await Task.Run(() => ReadXlsx(path), cancellationToken),
            ".ods" => await Task.Run(() => ReadOds(path), cancellationToken),
            ".csv" or ".txt" => await ReadDelimitedAsync(path, cancellationToken),
            _ => throw new InvalidOperationException("Formato não suportado. Use XLSX, ODS ou CSV.")
        };
    }

    private static List<List<string>> ReadXlsx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var sharedStrings = ReadSharedStrings(archive);
        var sheetPath = ResolveFirstWorksheetPath(archive);
        var entry = archive.GetEntry(sheetPath) ?? throw new InvalidDataException("A primeira planilha não foi encontrada no XLSX.");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var result = new List<List<string>>();
        foreach (var row in doc.Descendants(ns + "row"))
        {
            var values = new SortedDictionary<int, string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? string.Empty;
                var column = ColumnIndex(reference);
                var type = (string?)cell.Attribute("t") ?? string.Empty;
                string value;
                if (type == "inlineStr")
                    value = string.Concat(cell.Descendants(ns + "t").Select(x => x.Value));
                else
                {
                    var raw = cell.Element(ns + "v")?.Value ?? string.Empty;
                    value = type == "s" && int.TryParse(raw, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count
                        ? sharedStrings[sharedIndex]
                        : raw;
                }
                values[column] = value;
            }
            if (values.Count == 0) { result.Add([]); continue; }
            var list = Enumerable.Repeat(string.Empty, values.Keys.Max() + 1).ToList();
            foreach (var pair in values) list[pair.Key] = pair.Value;
            TrimTrailingEmpty(list);
            result.Add(list);
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
        return doc.Descendants(ns + "si")
            .Select(x => string.Concat(x.Descendants(ns + "t").Select(t => t.Value)))
            .ToList();
    }

    private static string ResolveFirstWorksheetPath(ZipArchive archive)
    {
        var workbook = archive.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("workbook.xml não encontrado.");
        var rels = archive.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("Relacionamentos do workbook não encontrados.");
        using var wbStream = workbook.Open();
        using var relStream = rels.Open();
        var wbDoc = XDocument.Load(wbStream);
        var relDoc = XDocument.Load(relStream);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";
        var id = (string?)wbDoc.Descendants(main + "sheet").FirstOrDefault()?.Attribute(r + "id")
            ?? throw new InvalidDataException("Nenhuma aba encontrada no XLSX.");
        var target = (string?)relDoc.Descendants(pr + "Relationship").FirstOrDefault(x => string.Equals((string?)x.Attribute("Id"), id, StringComparison.Ordinal))?.Attribute("Target")
            ?? throw new InvalidDataException("A aba do XLSX não possui relacionamento válido.");
        target = target.Replace('\\', '/').TrimStart('/');
        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
    }

    private static List<List<string>> ReadOds(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("content.xml") ?? throw new InvalidDataException("content.xml não encontrado no ODS.");
        using var stream = entry.Open();
        var doc = XDocument.Load(stream);
        XNamespace table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        var sheet = doc.Descendants(table + "table").FirstOrDefault() ?? throw new InvalidDataException("Nenhuma aba encontrada no ODS.");
        var result = new List<List<string>>();
        foreach (var row in sheet.Elements(table + "table-row"))
        {
            var rowRepeat = ParseRepeat((string?)row.Attribute(table + "number-rows-repeated"));
            var values = new List<string>();
            foreach (var cell in row.Elements().Where(x => x.Name == table + "table-cell" || x.Name == table + "covered-table-cell"))
            {
                var repeat = ParseRepeat((string?)cell.Attribute(table + "number-columns-repeated"));
                var value = string.Join("\n", cell.Descendants(text + "p").Select(x => string.Concat(x.DescendantNodes().OfType<XText>().Select(t => t.Value)))).Trim();
                for (var i = 0; i < Math.Min(repeat, 500); i++) values.Add(value);
            }
            TrimTrailingEmpty(values);
            for (var i = 0; i < Math.Min(rowRepeat, 500); i++) result.Add(values.ToList());
        }
        return result;
    }

    private static int ParseRepeat(string? value) => int.TryParse(value, out var result) && result > 0 ? result : 1;

    private static async Task<List<List<string>>> ReadDelimitedAsync(string path, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        var firstLine = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).FirstOrDefault() ?? string.Empty;
        var separator = firstLine.Count(x => x == ';') >= firstLine.Count(x => x == ',') ? ';' : ',';
        var result = new List<List<string>>();
        foreach (var line in ParseCsvLines(text, separator))
        {
            var row = line.ToList();
            TrimTrailingEmpty(row);
            result.Add(row);
        }
        return result;
    }

    private static IEnumerable<IReadOnlyList<string>> ParseCsvLines(string text, char separator)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = !quoted;
                continue;
            }
            if (!quoted && ch == separator) { row.Add(cell.ToString()); cell.Clear(); continue; }
            if (!quoted && (ch == '\r' || ch == '\n'))
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                yield return row.ToList(); row.Clear();
                continue;
            }
            cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); yield return row; }
    }

    private static void TrimTrailingEmpty(List<string> values)
    {
        while (values.Count > 0 && string.IsNullOrWhiteSpace(values[^1])) values.RemoveAt(values.Count - 1);
    }

    private static string BuildSheetXml(IReadOnlyList<string> headers, IReadOnlyList<double> widths, IReadOnlyList<SpreadsheetRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        var lastColumn = ColumnName(headers.Count);
        var lastRow = Math.Max(1, rows.Count + 1);
        sb.Append($"<dimension ref=\"A1:{lastColumn}{lastRow}\"/>");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("<sheetFormatPr defaultRowHeight=\"18\"/>");
        sb.Append("<cols>");
        for (var i = 0; i < widths.Count; i++)
            sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Math.Max(4, widths[i]).ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");
        sb.Append("<row r=\"1\" ht=\"26\" customHeight=\"1\">");
        for (var i = 0; i < headers.Count; i++) AppendCell(sb, CellRef(i + 1, 1), new SpreadsheetCell { Text = headers[i] }, 1);
        sb.Append("</row>");

        var excelRow = 2;
        var dataIndex = 0;
        foreach (var row in rows)
        {
            if (row.IsGroup)
            {
                sb.Append($"<row r=\"{excelRow}\" ht=\"23\" customHeight=\"1\">");
                AppendCell(sb, CellRef(1, excelRow), row.Cells.FirstOrDefault() ?? new SpreadsheetCell(), 4);
                sb.Append("</row>");
            }
            else
            {
                var style = dataIndex++ % 2 == 0 ? 2 : 3;
                sb.Append($"<row r=\"{excelRow}\" ht=\"22\" customHeight=\"1\">");
                for (var column = 0; column < headers.Count; column++)
                    AppendCell(sb, CellRef(column + 1, excelRow), column < row.Cells.Count ? row.Cells[column] : new SpreadsheetCell(), style);
                sb.Append("</row>");
            }
            excelRow++;
        }
        sb.Append("</sheetData>");

        // A ordem abaixo segue rigorosamente a sequência CT_Worksheet do OpenXML.
        // O Excel pode reparar/remover a planilha quando autoFilter, mergeCells ou
        // pageMargins/pageSetup são gravados fora dessa ordem, mesmo com XML bem-formado.
        sb.Append($"<autoFilter ref=\"A1:{lastColumn}{lastRow}\"/>");

        excelRow = 2;
        var mergeReferences = new List<string>();
        foreach (var row in rows)
        {
            if (row.IsGroup) mergeReferences.Add($"A{excelRow}:{lastColumn}{excelRow}");
            excelRow++;
        }
        if (mergeReferences.Count > 0)
        {
            sb.Append($"<mergeCells count=\"{mergeReferences.Count}\">");
            foreach (var reference in mergeReferences) sb.Append($"<mergeCell ref=\"{reference}\"/>");
            sb.Append("</mergeCells>");
        }
        sb.Append("<pageMargins left=\"0.25\" right=\"0.25\" top=\"0.45\" bottom=\"0.45\" header=\"0.2\" footer=\"0.2\"/>");
        sb.Append("<pageSetup orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static void AppendCell(StringBuilder sb, string reference, SpreadsheetCell cell, int style)
    {
        sb.Append($"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is>");
        if (cell.Runs is { Count: > 0 })
        {
            foreach (var run in cell.Runs)
            {
                sb.Append("<r><rPr><rFont val=\"Calibri\"/>");
                if (run.Bold) sb.Append("<b/>");
                sb.Append("<sz val=\"11\"/></rPr><t xml:space=\"preserve\">").Append(EscapeXml(run.Text)).Append("</t></r>");
            }
        }
        else sb.Append("<t xml:space=\"preserve\">").Append(EscapeXml(cell.Text)).Append("</t>");
        sb.Append("</is></c>");
    }

    private static string EscapeXml(string? value)
    {
        var safe = new string((value ?? string.Empty).Where(XmlConvert.IsXmlChar).ToArray());
        return SecurityElement.Escape(safe) ?? string.Empty;
    }

    private static void ValidateXlsx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var required in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" })
            if (archive.GetEntry(required) is null) throw new InvalidDataException("A planilha gerada ficou incompleta: " + required);
        foreach (var xmlEntry in archive.Entries.Where(x => x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || x.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = xmlEntry.Open();
            _ = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static int ColumnIndex(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        var result = 0;
        foreach (var ch in letters) result = result * 26 + (ch - 'A' + 1);
        return Math.Max(0, result - 1);
    }

    private static string CellRef(int column, int row) => ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);
    private static string ColumnName(int number)
    {
        var name = string.Empty;
        while (number > 0) { number--; name = (char)('A' + number % 26) + name; number /= 26; }
        return name;
    }

    private static string BuildWorkbookXml(string sheetName)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{EscapeXml(TrimSheetName(sheetName))}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

    private static string TrimSheetName(string value)
    {
        var invalid = new HashSet<char>(new[] { '[', ']', ':', '*', '?', '/', '\\' });
        var safe = new string((value ?? "Planilha").Where(x => !invalid.Contains(x)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Planilha" : safe[..Math.Min(31, safe.Length)];
    }

    private static string BuildCoreXml(string title)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><dc:title>{EscapeXml(title)}</dc:title><dc:creator>SIGFUR</dc:creator><cp:lastModifiedBy>SIGFUR</cp:lastModifiedBy><dcterms:created xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:O}</dcterms:created></cp:coreProperties>";

    private const string ContentTypesXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/><Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/><Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/></Types>";
    private const string RootRelsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>";
    private const string WorkbookRelsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
    private const string StylesXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/></font></fonts><fills count=\"5\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E79\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF4F8FC\"/><bgColor indexed=\"64\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF2F75B5\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border><border><left style=\"thin\"><color rgb=\"FFD9E2F3\"/></left><right style=\"thin\"><color rgb=\"FFD9E2F3\"/></right><top style=\"thin\"><color rgb=\"FFD9E2F3\"/></top><bottom style=\"thin\"><color rgb=\"FFD9E2F3\"/></bottom><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"5\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"1\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
    private const string AppXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\"><Application>SIGFUR</Application></Properties>";
}
