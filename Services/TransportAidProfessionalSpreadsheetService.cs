using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SIGFUR.Wpf.Services;

public sealed record TransportAidProfessionalReportRow(
    string Rank,
    string FullName,
    string Document,
    string ReceivesTransportAid,
    string ValueRange,
    decimal NetValue,
    string HasVacation,
    string VacationPeriod,
    string VacationDaNoteStatus,
    decimal ExpenseToCancel,
    string Observation);

public sealed record TransportAidProfessionalReport(
    string Competence,
    string Filter,
    string DocumentHeader,
    DateTime GeneratedAt,
    IReadOnlyList<TransportAidProfessionalReportRow> Rows);

/// <summary>
/// Gera a planilha gerencial do Auxílio-Transporte sem depender do Excel instalado.
/// O arquivo possui painel executivo, relação auditável, filtros, fórmulas e alertas visuais.
/// </summary>
public sealed class TransportAidProfessionalSpreadsheetService
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public Task ExportAsync(string path, TransportAidProfessionalReport report, CancellationToken cancellationToken = default)
        => Task.Run(() => WriteWorkbook(path, report), cancellationToken);

    private static void WriteWorkbook(string path, TransportAidProfessionalReport report)
    {
        if (report.Rows.Count == 0) throw new InvalidOperationException("O relatório não possui militares para exportar.");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(path)) File.Delete(path);

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
        WriteEntry(archive, "_rels/.rels", RootRelationshipsXml);
        WriteEntry(archive, "xl/workbook.xml", WorkbookXml);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml);
        WriteEntry(archive, "xl/styles.xml", StylesXml);
        WriteDocument(archive, "xl/worksheets/sheet1.xml", BuildSummarySheet(report));
        WriteDocument(archive, "xl/worksheets/sheet2.xml", BuildDetailsSheet(report));
    }

    private static XDocument BuildSummarySheet(TransportAidProfessionalReport report)
    {
        var rows = report.Rows;
        var firstDataRow = 6;
        var lastDataRow = firstDataRow + rows.Count - 1;
        var pending = rows.Where(x => x.HasVacation == "Sim" && x.VacationDaNoteStatus == "Pendente").ToList();
        var receives = rows.Count(x => x.ReceivesTransportAid == "Sim");
        var vacationCount = rows.Count(x => x.HasVacation == "Sim");
        var generatedCount = rows.Count(x => x.VacationDaNoteStatus == "Gerada no SisBol");
        var netTotal = rows.Sum(x => x.NetValue);
        var daTotal = rows.Sum(x => x.ExpenseToCancel);
        var data = new XElement(SpreadsheetNs + "sheetData");

        data.Add(Row(1, 30, Inline("A1", "RELATÓRIO GERENCIAL DE AUXÍLIO-TRANSPORTE", 1)));
        data.Add(Row(2, 22, Inline("A2", $"Competência {report.Competence} • SAT Maior • Controle de férias e Despesa a Anular", 1)));
        data.Add(Row(3, 20, Inline("A3", $"Gerado em {report.GeneratedAt.ToString("dd/MM/yyyy 'às' HH:mm", PtBr)} • Filtro: {report.Filter}", 2)));

        data.Add(Row(5, 22,
            Inline("A5", "MILITARES", 4), Inline("C5", "RECEBEM AT", 4), Inline("E5", "EM FÉRIAS", 4), Inline("G5", "NOTAS PENDENTES", 4)));
        data.Add(Row(6, 31,
            Formula("A6", $"COUNTA('Relação SAT'!B{firstDataRow}:B{lastDataRow})", rows.Count, 5),
            Formula("C6", $"COUNTIF('Relação SAT'!D{firstDataRow}:D{lastDataRow},\"Sim\")", receives, 5),
            Formula("E6", $"COUNTIF('Relação SAT'!G{firstDataRow}:G{lastDataRow},\"Sim\")", vacationCount, 5),
            Formula("G6", $"COUNTIF('Relação SAT'!I{firstDataRow}:I{lastDataRow},\"Pendente\")", pending.Count, pending.Count > 0 ? 15u : 14u)));

        data.Add(Row(8, 22,
            Inline("A8", "VALOR LÍQUIDO DO AT", 4), Inline("E8", "DESPESA A ANULAR", 4)));
        data.Add(Row(9, 31,
            Formula("A9", $"SUM('Relação SAT'!F{firstDataRow}:F{lastDataRow})", netTotal, 6),
            Formula("E9", $"SUM('Relação SAT'!J{firstDataRow}:J{lastDataRow})", daTotal, 6)));

        data.Add(Row(11, 25, Inline("A11", "PENDÊNCIAS PRIORITÁRIAS — NOTA DE DA DO AUXÍLIO-TRANSPORTE", 3)));
        data.Add(Row(12, 25,
            Inline("A12", "P/G", 7), Inline("B12", "Nome completo", 7), Inline("C12", "Período de férias", 7),
            Inline("D12", "Despesa a anular", 7), Inline("E12", "Situação da nota", 7)));

        var currentRow = 13;
        if (pending.Count == 0)
        {
            data.Add(Row(currentRow, 28, Inline($"A{currentRow}", "Nenhuma pendência de nota de DA para as férias encontradas nesta competência.", 14)));
        }
        else
        {
            foreach (var item in pending.OrderByDescending(x => x.ExpenseToCancel).ThenBy(x => x.FullName))
            {
                data.Add(Row(currentRow, 28,
                    Inline($"A{currentRow}", item.Rank, 8), Inline($"B{currentRow}", item.FullName, 18),
                    Inline($"C{currentRow}", item.VacationPeriod, 18), Number($"D{currentRow}", item.ExpenseToCancel, 10),
                    Inline($"E{currentRow}", item.VacationDaNoteStatus, 15)));
                currentRow++;
            }
        }

        currentRow += 2;
        data.Add(Row(currentRow, 36, Inline($"A{currentRow}", "Critério: a nota é considerada gerada somente quando a publicação complementar de Despesa a Anular por férias foi enviada ao SisBol e salva no histórico do Plano de Férias.", 2)));

        var worksheet = new XElement(SpreadsheetNs + "worksheet",
            new XElement(SpreadsheetNs + "sheetViews",
                new XElement(SpreadsheetNs + "sheetView", new XAttribute("workbookViewId", 0), new XAttribute("showGridLines", 0))),
            new XElement(SpreadsheetNs + "sheetFormatPr", new XAttribute("defaultRowHeight", 20)),
            Columns((1, 12d), (2, 39d), (3, 30d), (4, 20d), (5, 25d), (6, 4d), (7, 22d), (8, 4d)),
            data,
            new XElement(SpreadsheetNs + "mergeCells", new XAttribute("count", pending.Count == 0 ? 18 : 17),
                Merge("A1:H1"), Merge("A2:H2"), Merge("A3:H3"),
                Merge("A5:B5"), Merge("C5:D5"), Merge("E5:F5"), Merge("G5:H5"),
                Merge("A6:B6"), Merge("C6:D6"), Merge("E6:F6"), Merge("G6:H6"),
                Merge("A8:D8"), Merge("E8:H8"), Merge("A9:D9"), Merge("E9:H9"),
                Merge("A11:H11"),
                pending.Count == 0 ? Merge("A13:E13") : null,
                Merge($"A{currentRow}:H{currentRow}")),
            new XElement(SpreadsheetNs + "pageMargins", new XAttribute("left", 0.3), new XAttribute("right", 0.3), new XAttribute("top", 0.5), new XAttribute("bottom", 0.5), new XAttribute("header", 0.2), new XAttribute("footer", 0.2)),
            new XElement(SpreadsheetNs + "pageSetup", new XAttribute("orientation", "landscape"), new XAttribute("fitToWidth", 1), new XAttribute("fitToHeight", 0)));
        return Document(worksheet);
    }

    private static XDocument BuildDetailsSheet(TransportAidProfessionalReport report)
    {
        var data = new XElement(SpreadsheetNs + "sheetData");
        data.Add(Row(1, 30, Inline("A1", "RELAÇÃO PARA CONFERÊNCIA — AUXÍLIO-TRANSPORTE", 1)));
        data.Add(Row(2, 22, Inline("A2", $"Competência {report.Competence} • Controle da SAT Maior", 1)));
        data.Add(Row(3, 20, Inline("A3", $"Gerado em {report.GeneratedAt.ToString("dd/MM/yyyy 'às' HH:mm", PtBr)} • Filtro aplicado: {report.Filter}", 2)));
        data.Add(Row(5, 32,
            Inline("A5", "P/G", 7), Inline("B5", "Nome completo", 7), Inline("C5", report.DocumentHeader, 7),
            Inline("D5", "Recebe AT", 7), Inline("E5", "Faixa de valor", 7), Inline("F5", "Valor líquido", 7),
            Inline("G5", "Férias", 7), Inline("H5", "Período de férias", 7), Inline("I5", "Nota DA do AT", 7),
            Inline("J5", "Despesa a anular", 7), Inline("K5", "Observação / fundamento", 7)));

        var rowIndex = 6;
        foreach (var item in report.Rows)
        {
            var odd = rowIndex % 2 == 0;
            var body = odd ? 8u : 9u;
            var wrap = odd ? 18u : 19u;
            var currency = odd ? 10u : 11u;
            var receiveStyle = item.ReceivesTransportAid == "Sim" ? 14u : 17u;
            var vacationStyle = item.HasVacation == "Sim" ? 16u : 17u;
            var noteStyle = item.VacationDaNoteStatus switch
            {
                "Gerada no SisBol" => 14u,
                "Pendente" => 15u,
                _ => 17u
            };
            data.Add(Row(rowIndex, 36,
                Inline($"A{rowIndex}", item.Rank, body), Inline($"B{rowIndex}", item.FullName, wrap),
                Inline($"C{rowIndex}", item.Document, body), Inline($"D{rowIndex}", item.ReceivesTransportAid, receiveStyle),
                Inline($"E{rowIndex}", item.ValueRange, body), Number($"F{rowIndex}", item.NetValue, currency),
                Inline($"G{rowIndex}", item.HasVacation, vacationStyle), Inline($"H{rowIndex}", item.VacationPeriod, wrap),
                Inline($"I{rowIndex}", item.VacationDaNoteStatus, noteStyle), Number($"J{rowIndex}", item.ExpenseToCancel, currency),
                Inline($"K{rowIndex}", item.Observation, wrap)));
            rowIndex++;
        }

        var firstDataRow = 6;
        var lastDataRow = rowIndex - 1;
        data.Add(Row(rowIndex, 28,
            Inline($"A{rowIndex}", "TOTAIS", 12),
            Formula($"F{rowIndex}", $"SUM(F{firstDataRow}:F{lastDataRow})", report.Rows.Sum(x => x.NetValue), 13),
            Formula($"J{rowIndex}", $"SUM(J{firstDataRow}:J{lastDataRow})", report.Rows.Sum(x => x.ExpenseToCancel), 13)));
        var totalsRow = rowIndex;
        rowIndex += 2;
        data.Add(Row(rowIndex, 38, Inline($"A{rowIndex}", "Legenda: verde = concluído/recebe; amarelo = férias identificadas; vermelho = nota de DA pendente; cinza = não se aplica. A Despesa a Anular considera a DA salva e as férias coincidentes com a competência, limitada ao valor mensal do SAT para evitar duplicidade.", 2)));

        var worksheet = new XElement(SpreadsheetNs + "worksheet",
            new XElement(SpreadsheetNs + "sheetViews",
                new XElement(SpreadsheetNs + "sheetView", new XAttribute("workbookViewId", 0), new XAttribute("showGridLines", 0),
                    new XElement(SpreadsheetNs + "pane", new XAttribute("ySplit", 5), new XAttribute("topLeftCell", "A6"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
            new XElement(SpreadsheetNs + "sheetFormatPr", new XAttribute("defaultRowHeight", 20)),
            Columns((1, 10d), (2, 40d), (3, 18d), (4, 13d), (5, 24d), (6, 16d), (7, 11d), (8, 31d), (9, 22d), (10, 19d), (11, 58d)),
            data,
            new XElement(SpreadsheetNs + "autoFilter", new XAttribute("ref", $"A5:K{lastDataRow}")),
            new XElement(SpreadsheetNs + "mergeCells", new XAttribute("count", 5),
                Merge("A1:K1"), Merge("A2:K2"), Merge("A3:K3"), Merge($"A{totalsRow}:E{totalsRow}"), Merge($"A{rowIndex}:K{rowIndex}")),
            new XElement(SpreadsheetNs + "pageMargins", new XAttribute("left", 0.2), new XAttribute("right", 0.2), new XAttribute("top", 0.45), new XAttribute("bottom", 0.45), new XAttribute("header", 0.2), new XAttribute("footer", 0.2)),
            new XElement(SpreadsheetNs + "pageSetup", new XAttribute("orientation", "landscape"), new XAttribute("paperSize", 9), new XAttribute("fitToWidth", 1), new XAttribute("fitToHeight", 0)));
        return Document(worksheet);
    }

    private static XElement Row(int index, double height, params XElement[] cells)
        => new(SpreadsheetNs + "row", new XAttribute("r", index), new XAttribute("ht", height.ToString(Invariant)), new XAttribute("customHeight", 1), cells);

    private static XElement Inline(string reference, string? value, uint style)
        => new(SpreadsheetNs + "c", new XAttribute("r", reference), new XAttribute("s", style), new XAttribute("t", "inlineStr"),
            new XElement(SpreadsheetNs + "is", new XElement(SpreadsheetNs + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value ?? string.Empty)));

    private static XElement Number(string reference, decimal value, uint style)
        => new(SpreadsheetNs + "c", new XAttribute("r", reference), new XAttribute("s", style), new XAttribute("t", "n"),
            new XElement(SpreadsheetNs + "v", value.ToString("0.00", Invariant)));

    private static XElement Formula(string reference, string formula, decimal cachedValue, uint style)
        => new(SpreadsheetNs + "c", new XAttribute("r", reference), new XAttribute("s", style),
            new XElement(SpreadsheetNs + "f", formula), new XElement(SpreadsheetNs + "v", cachedValue.ToString("0.00", Invariant)));

    private static XElement Columns(params (int Index, double Width)[] definitions)
        => new(SpreadsheetNs + "cols", definitions.Select(x => new XElement(SpreadsheetNs + "col",
            new XAttribute("min", x.Index), new XAttribute("max", x.Index),
            new XAttribute("width", x.Width.ToString(Invariant)), new XAttribute("customWidth", 1))));

    private static XElement Merge(string reference) => new(SpreadsheetNs + "mergeCell", new XAttribute("ref", reference));

    private static XDocument Document(XElement root) => new(new XDeclaration("1.0", "UTF-8", "yes"), root);

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content.Trim());
    }

    private static void WriteDocument(ZipArchive archive, string name, XDocument document)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false });
        document.Save(writer);
    }

    private const string ContentTypesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
          <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
        </Types>
        """;

    private const string RootRelationshipsXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string WorkbookXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <workbookPr date1904="0"/>
          <sheets>
            <sheet name="Resumo SAT" sheetId="1" r:id="rId1"/>
            <sheet name="Relação SAT" sheetId="2" r:id="rId2"/>
          </sheets>
          <calcPr calcId="191029" calcMode="auto" fullCalcOnLoad="1" forceFullCalc="1"/>
        </workbook>
        """;

    private const string WorkbookRelationshipsXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
          <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
        </Relationships>
        """;

    private const string StylesXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="1"><numFmt numFmtId="164" formatCode="&quot;R$&quot; #,##0.00"/></numFmts>
          <fonts count="8">
            <font><sz val="10"/><name val="Aptos"/><family val="2"/></font>
            <font><b/><color rgb="FFFFFFFF"/><sz val="17"/><name val="Aptos Display"/><family val="2"/></font>
            <font><color rgb="FF5B6573"/><sz val="10"/><name val="Aptos"/><family val="2"/></font>
            <font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Aptos"/><family val="2"/></font>
            <font><b/><color rgb="FF17365D"/><sz val="10"/><name val="Aptos"/><family val="2"/></font>
            <font><b/><color rgb="FF17365D"/><sz val="16"/><name val="Aptos Display"/><family val="2"/></font>
            <font><b/><color rgb="FF006100"/><sz val="10"/><name val="Aptos"/><family val="2"/></font>
            <font><b/><color rgb="FF9C0006"/><sz val="10"/><name val="Aptos"/><family val="2"/></font>
          </fonts>
          <fills count="10">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF17365D"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E78"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFD9EAF7"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFF4F8FC"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFE2F0D9"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFCE4D6"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFFF2CC"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFE7E6E6"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left style="thin"><color rgb="FFD5DFEA"/></left><right style="thin"><color rgb="FFD5DFEA"/></right><top style="thin"><color rgb="FFD5DFEA"/></top><bottom style="thin"><color rgb="FFD5DFEA"/></bottom><diagonal/></border>
          </borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="20">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"><alignment vertical="center"/></xf>
            <xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"><alignment vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="3" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment vertical="center"/></xf>
            <xf numFmtId="0" fontId="4" fillId="4" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center"/></xf>
            <xf numFmtId="0" fontId="5" fillId="4" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center"/></xf>
            <xf numFmtId="164" fontId="5" fillId="4" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center"/></xf>
            <xf numFmtId="0" fontId="3" fillId="3" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0"><alignment vertical="center"/></xf>
            <xf numFmtId="0" fontId="0" fillId="5" borderId="1" xfId="0" applyFill="1"><alignment vertical="center"/></xf>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1"><alignment horizontal="right" vertical="center"/></xf>
            <xf numFmtId="164" fontId="0" fillId="5" borderId="1" xfId="0" applyNumberFormat="1" applyFill="1"><alignment horizontal="right" vertical="center"/></xf>
            <xf numFmtId="0" fontId="3" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment vertical="center"/></xf>
            <xf numFmtId="164" fontId="3" fillId="2" borderId="1" xfId="0" applyNumberFormat="1" applyFont="1" applyFill="1"><alignment horizontal="right" vertical="center"/></xf>
            <xf numFmtId="0" fontId="6" fillId="6" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="7" fillId="7" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="4" fillId="8" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="2" fillId="9" borderId="1" xfId="0" applyFont="1" applyFill="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0"><alignment vertical="center" wrapText="1"/></xf>
            <xf numFmtId="0" fontId="0" fillId="5" borderId="1" xfId="0" applyFill="1"><alignment vertical="center" wrapText="1"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;
}
