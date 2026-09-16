using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>Gera e lê a planilha oficial de ordenação do efetivo.</summary>
public sealed partial class MilitaryOrderSpreadsheetService
{
    public Task ExportAsync(string path, IReadOnlyList<MilitaryRecord> military, string scopeName)
    {
        if (military.Count == 0) throw new InvalidOperationException("Não há militares para colocar na planilha.");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = CreateStylesheet();
        stylesPart.Stylesheet.Save();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        AddOrderSheet(workbookPart, sheets, military, scopeName);
        AddInstructionsSheet(workbookPart, sheets);
        workbookPart.Workbook.Save();
        return Task.CompletedTask;
    }

    public Task<MilitaryOrderImportResult> ImportAsync(string path, IReadOnlyList<MilitaryRecord> current)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("A pasta de trabalho não possui conteúdo legível.");
        var sheets = ReadSheets(workbookPart);
        if (sheets.Count == 0) throw new InvalidDataException("Nenhuma aba legível foi encontrada.");

        var template = TryReadSigfurTemplate(sheets);
        if (template is not null)
            return Task.FromResult(MatchTemplate(template.Value.Sheet, template.Value.Rows, current));

        var names = ReadOperationalNames(sheets, out var sourceSheet, out var sourceKind);
        if (names.Count == 0)
            throw new InvalidDataException("Não encontrei uma coluna de nomes nem uma relação compatível nas abas da planilha.");
        return Task.FromResult(MatchNames(sourceSheet, sourceKind, names, current));
    }

    private static void AddOrderSheet(WorkbookPart workbookPart, Sheets sheets, IReadOnlyList<MilitaryRecord> military, string scopeName)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        var view = new SheetView { WorkbookViewId = 0U };
        view.Append(new Pane { VerticalSplit = 4D, TopLeftCell = "A5", ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen });
        var worksheet = new Worksheet(
            new SheetViews(view),
            new SheetFormatProperties { DefaultRowHeight = 18D },
            new Columns(Col(1, 11), Col(2, 13), Col(3, 22), Col(4, 12), Col(5, 25), Col(6, 48), Col(7, 34)),
            data);

        data.Append(MakeRow(1, 27, TextCell("A1", "SIGFUR — PLANILHA DE ORDEM DO EFETIVO", 1)));
        data.Append(MakeRow(2, 22, TextCell("A2", $"Escopo: {scopeName} • Gerada em {DateTime.Now:dd/MM/yyyy HH:mm}", 2)));
        data.Append(MakeRow(3, 34, TextCell("A3", "Altere os números da coluna ORDEM ou mova as linhas inteiras. Não altere o ID SIGFUR. Depois use “Importar ordem” no Listar Militares.", 7)));
        data.Append(MakeRow(4, 24,
            TextCell("A4", "ORDEM", 3), TextCell("B4", "ID SIGFUR", 3),
            TextCell("C4", "POSTO/GRADUAÇÃO", 3), TextCell("D4", "ANO", 3),
            TextCell("E4", "NOME DE GUERRA", 3), TextCell("F4", "NOME COMPLETO", 3),
            TextCell("G4", "OBSERVAÇÃO", 3)));

        for (var index = 0; index < military.Count; index++)
        {
            var item = military[index];
            var row = (uint)index + 5U;
            data.Append(MakeRow(row, 21,
                NumberCell($"A{row}", index + 1, 4), NumberCell($"B{row}", item.Id, 5),
                TextCell($"C{row}", item.ShortRank, 6), TextCell($"D{row}", item.FormationYear, 6),
                TextCell($"E{row}", item.WarName, 6), TextCell($"F{row}", item.Name, 6),
                TextCell($"G{row}", string.Empty, 6)));
        }

        worksheet.Append(new AutoFilter { Reference = $"A4:G{military.Count + 4}" });
        worksheet.Append(new MergeCells(new MergeCell { Reference = "A1:G1" }, new MergeCell { Reference = "A2:G2" }, new MergeCell { Reference = "A3:G3" }));
        worksheet.Append(new PageMargins { Left = .25D, Right = .25D, Top = .5D, Bottom = .5D, Header = .2D, Footer = .2D });
        part.Worksheet = worksheet;
        part.Worksheet.Save();
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = 1U, Name = "ORDEM SIGFUR" });
    }

    private static void AddInstructionsSheet(WorkbookPart workbookPart, Sheets sheets)
    {
        var part = workbookPart.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        var worksheet = new Worksheet(new SheetFormatProperties { DefaultRowHeight = 20D }, new Columns(Col(1, 6), Col(2, 105)), data);
        data.Append(MakeRow(1, 28, TextCell("A1", "COMO USAR", 1)));
        var instructions = new[]
        {
            "Abra a aba ORDEM SIGFUR.",
            "Defina a sequência alterando os números da coluna ORDEM. Também é permitido mover as linhas inteiras.",
            "Não altere o ID SIGFUR: ele identifica a pessoa sem depender de abreviações ou nomes repetidos.",
            "Salve o arquivo em formato .xlsx.",
            "No Listar Militares, clique em Importar ordem e selecione esta planilha.",
            "O SIGFUR cria uma nova lista salva; nenhum dado do cadastro é modificado.",
            "Planilhas externas com aba EFETIVO ou ESCALA 2026 também podem ser lidas por nome de guerra, mas o modelo SIGFUR é mais preciso."
        };
        for (var index = 0; index < instructions.Length; index++)
        {
            var row = (uint)index + 3U;
            data.Append(MakeRow(row, index == 6 ? 38 : 25, NumberCell($"A{row}", index + 1, 4), TextCell($"B{row}", instructions[index], index == 6 ? 7U : 6U)));
        }
        worksheet.Append(new MergeCells(new MergeCell { Reference = "A1:B1" }));
        worksheet.Append(new PageMargins { Left = .35D, Right = .35D, Top = .5D, Bottom = .5D, Header = .2D, Footer = .2D });
        part.Worksheet = worksheet;
        part.Worksheet.Save();
        sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(part), SheetId = 2U, Name = "LEIA-ME" });
    }

    private static MilitaryOrderImportResult MatchTemplate(string sheet, IReadOnlyList<TemplateRow> rows, IReadOnlyList<MilitaryRecord> current)
    {
        var ids = current.Where(x => x.Id > 0).Select(x => x.Id).ToHashSet();
        var valid = rows.Where(x => x.Id > 0).OrderBy(x => x.Order ?? int.MaxValue).ThenBy(x => x.RowIndex).DistinctBy(x => x.Id).ToList();
        var ordered = valid.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToList();
        return new MilitaryOrderImportResult
        {
            SourceSheet = sheet, SourceKind = "Modelo SIGFUR por ID", IsSigfurTemplate = true,
            OrderedIds = ordered, SourceEntries = valid.Count, MatchedCount = ordered.Count,
            IgnoredIds = valid.Count - ordered.Count
        };
    }

    private static MilitaryOrderImportResult MatchNames(string sheet, string sourceKind, IReadOnlyList<ImportedName> names, IReadOnlyList<MilitaryRecord> current)
    {
        var result = new MilitaryOrderImportResult { SourceSheet = sheet, SourceKind = sourceKind, SourceEntries = names.Count };
        var used = new HashSet<int>();
        var ordered = new List<int>();
        foreach (var source in names)
        {
            var wanted = NormalizeName(source.Name);
            var wantedWords = Normalize(CleanOperationalName(source.Name));
            if (wanted.Length == 0) continue;
            var available = current.Where(x => x.Id > 0 && !used.Contains(x.Id)).ToList();
            var candidates = available.Where(x => NormalizeName(x.WarName) == wanted).ToList();
            if (candidates.Count == 0) candidates = available.Where(x => NormalizeName(x.Name) == wanted).ToList();
            if (candidates.Count == 0 && wantedWords.Length >= 4)
                candidates = available.Where(x => $" {Normalize(x.Name)} ".Contains($" {wantedWords} ", StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0 && wanted.Length >= 4)
                candidates = available.Where(x => { var war = NormalizeName(x.WarName); return war.Length >= 4 && (war.Contains(wanted) || wanted.Contains(war)); }).ToList();
            if (!string.IsNullOrWhiteSpace(source.Rank))
            {
                var ranked = candidates.Where(x => RankCompatible(source.Rank, x.Rank)).ToList();
                if (ranked.Count > 0) candidates = ranked;
            }
            if (candidates.Count == 1)
            {
                ordered.Add(candidates[0].Id);
                used.Add(candidates[0].Id);
            }
            else if (candidates.Count > 1) result.AmbiguousNames.Add($"{source.Rank} {source.Name}".Trim());
            else result.UnmatchedNames.Add($"{source.Rank} {source.Name}".Trim());
        }

        var unmatched = current.Where(x => x.Id > 0 && !used.Contains(x.Id)).ToList();
        var officers = unmatched.Where(IsOfficer).OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var others = unmatched.Where(x => !IsOfficer(x)).ToList();
        result.OfficersPreserved = officers.Count;
        result.AppendedCount = officers.Count + others.Count;
        result.MatchedCount = ordered.Count;
        result.OrderedIds = officers.Select(x => x.Id).Concat(ordered).Concat(others.Select(x => x.Id)).Distinct().ToList();
        return result;
    }

    private static (string Sheet, List<TemplateRow> Rows)? TryReadSigfurTemplate(IReadOnlyList<SheetRows> sheets)
    {
        foreach (var sheet in sheets.OrderByDescending(x => Normalize(x.Name).Contains("ORDEM SIGFUR")))
        {
        var hasTemplateIdentity = Normalize(sheet.Name).Contains("ORDEM SIGFUR")
            || sheet.Rows.Take(5).SelectMany(x => x).Any(x => Normalize(x).Contains("SIGFUR PLANILHA DE ORDEM"));
        if (!hasTemplateIdentity) continue;
        for (var headerIndex = 0; headerIndex < Math.Min(sheet.Rows.Count, 40); headerIndex++)
        {
            var header = sheet.Rows[headerIndex];
            var idColumn = FindColumn(header, "ID SIGFUR");
            var orderColumn = FindColumn(header, "ORDEM", "POSICAO");
            var warNameColumn = FindColumn(header, "NOME DE GUERRA");
            var fullNameColumn = FindColumn(header, "NOME COMPLETO");
            if (idColumn < 0 || orderColumn < 0 || warNameColumn < 0 || fullNameColumn < 0) continue;
            var rows = new List<TemplateRow>();
            for (var index = headerIndex + 1; index < sheet.Rows.Count; index++)
            {
                if (!TryPositiveInt(Cell(sheet.Rows[index], idColumn), out var id)) continue;
                int? order = TryPositiveInt(Cell(sheet.Rows[index], orderColumn), out var position) ? position : null;
                rows.Add(new TemplateRow(index + 1, id, order));
            }
            if (rows.Count > 0) return (sheet.Name, rows);
        }
        }
        return null;
    }

    private static List<ImportedName> ReadOperationalNames(IReadOnlyList<SheetRows> sheets, out string sourceSheet, out string sourceKind)
    {
        var effective = sheets.FirstOrDefault(x => Normalize(x.Name).Contains("EFETIVO"));
        if (effective is not null)
        {
            var rows = ReadEffectiveMatrix(effective);
            if (rows.Count > 0) { sourceSheet = effective.Name; sourceKind = "Relação da aba EFETIVO"; return rows; }
        }
        var scale = sheets.FirstOrDefault(x => Normalize(x.Name).Contains("ESCALA"));
        if (scale is not null)
        {
            var rows = ReadScaleRelation(scale);
            if (rows.Count > 0) { sourceSheet = scale.Name; sourceKind = "Relação numerada da escala"; return rows; }
        }
        foreach (var sheet in sheets)
        {
            var rows = ReadSimpleNameTable(sheet);
            if (rows.Count > 0) { sourceSheet = sheet.Name; sourceKind = "Tabela por nome de guerra"; return rows; }
        }
        sourceSheet = string.Empty; sourceKind = string.Empty; return [];
    }

    private static List<ImportedName> ReadEffectiveMatrix(SheetRows sheet)
    {
        for (var headerIndex = 0; headerIndex < sheet.Rows.Count; headerIndex++)
        {
            var header = sheet.Rows[headerIndex];
            var rankColumn = FindColumn(header, "POSTO GRAD", "POSTO", "GRAD");
            var nameColumn = FindColumn(header, "NOME DE GUERRA");
            if (rankColumn < 0 || nameColumn < 0) continue;
            var result = new List<ImportedName>();
            var rank = string.Empty;
            for (var index = headerIndex + 1; index < sheet.Rows.Count; index++)
            {
                var first = Cell(sheet.Rows[index], rankColumn).Trim();
                if (Normalize(first) == "TOTAL") break;
                if (!string.IsNullOrWhiteSpace(first) && !double.TryParse(first, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) rank = first;
                if (string.IsNullOrWhiteSpace(rank)) continue;
                // A aba EFETIVO organiza até cinco nomes entre C e K, com células
                // mescladas de larguras diferentes. O limite evita quadros auxiliares à direita.
                var lastNameColumn = Math.Min(sheet.Rows[index].Count - 1, nameColumn + 8);
                for (var column = nameColumn; column <= lastNameColumn; column++)
                {
                    var raw = Cell(sheet.Rows[index], column).Trim();
                    if (LooksLikePersonName(raw)) result.Add(new ImportedName(CleanOperationalName(raw), rank, index + 1));
                }
            }
            if (result.Count > 0) return result;
        }
        return [];
    }

    private static List<ImportedName> ReadScaleRelation(SheetRows sheet)
    {
        var result = new List<ImportedName>();
        foreach (var pair in sheet.Rows.Select((row, index) => (row, index)))
        {
            var name = Cell(pair.row, 2).Trim();
            if (Normalize(name).StartsWith("OUTROS")) break;
            if (!TryPositiveInt(Cell(pair.row, 0), out _)) continue;
            var rank = Cell(pair.row, 1).Trim();
            if (!string.IsNullOrWhiteSpace(rank) && LooksLikePersonName(name)) result.Add(new ImportedName(CleanOperationalName(name), rank, pair.index + 1));
        }
        return result;
    }

    private static List<ImportedName> ReadSimpleNameTable(SheetRows sheet)
    {
        for (var headerIndex = 0; headerIndex < Math.Min(sheet.Rows.Count, 80); headerIndex++)
        {
            var header = sheet.Rows[headerIndex];
            var nameColumn = FindColumn(header, "NOME DE GUERRA", "NOME");
            if (nameColumn < 0) continue;
            var rankColumn = FindColumn(header, "POSTO GRAD", "POSTO", "GRAD");
            var orderColumn = FindColumn(header, "ORDEM", "POSICAO", "N ORD");
            var result = new List<(ImportedName Item, int? Order)>();
            for (var index = headerIndex + 1; index < sheet.Rows.Count; index++)
            {
                var name = Cell(sheet.Rows[index], nameColumn).Trim();
                if (!LooksLikePersonName(name)) continue;
                int? order = TryPositiveInt(Cell(sheet.Rows[index], orderColumn), out var value) ? value : null;
                result.Add((new ImportedName(CleanOperationalName(name), Cell(sheet.Rows[index], rankColumn), index + 1), order));
            }
            if (result.Count > 0) return result.OrderBy(x => x.Order ?? int.MaxValue).ThenBy(x => x.Item.RowIndex).Select(x => x.Item).ToList();
        }
        return [];
    }

    private static List<SheetRows> ReadSheets(WorkbookPart workbookPart)
    {
        var shared = workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(x => x.InnerText).ToList() ?? [];
        var result = new List<SheetRows>();
        var workbook = workbookPart.Workbook;
        var workbookSheets = workbook?.Sheets;
        if (workbookSheets is null) return result;
        foreach (var sheet in workbookSheets.Elements<Sheet>())
        {
            if (sheet.Id?.Value is not string id || workbookPart.GetPartById(id) is not WorksheetPart part) continue;
            var worksheet = part.Worksheet;
            if (worksheet is null) continue;
            var rows = new List<List<string>>();
            foreach (var row in worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>())
            {
                var values = new SortedDictionary<int, string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = ColumnIndex(cell.CellReference?.Value);
                    if (column >= 0) values[column] = CellText(cell, shared);
                }
                var materialized = Enumerable.Repeat(string.Empty, values.Count == 0 ? 0 : values.Keys.Max() + 1).ToList();
                foreach (var value in values) materialized[value.Key] = value.Value;
                rows.Add(materialized);
            }
            result.Add(new SheetRows(sheet.Name?.Value ?? "Planilha", rows));
        }
        return result;
    }

    private static string CellText(Cell cell, IReadOnlyList<string> shared)
    {
        if (cell.DataType?.Value == CellValues.InlineString) return cell.InlineString?.InnerText ?? string.Empty;
        var value = cell.CellValue?.Text ?? cell.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(value, out var index) && index >= 0 && index < shared.Count) return shared[index];
        if (cell.DataType?.Value == CellValues.Boolean) return value == "1" ? "Sim" : "Não";
        return value;
    }

    private static int FindColumn(IReadOnlyList<string> row, params string[] names)
    {
        var wanted = names.Select(Normalize).Where(x => x.Length > 0).ToList();
        for (var index = 0; index < row.Count; index++) if (wanted.Any(x => Normalize(row[index]) == x)) return index;
        for (var index = 0; index < row.Count; index++)
        {
            var current = Normalize(row[index]);
            if (current.Length > 0 && wanted.Any(x => current.Contains(x) || x.Contains(current))) return index;
        }
        return -1;
    }

    private static bool RankCompatible(string source, string current)
    {
        var a = MilitaryRankService.GetOrder(source); var b = MilitaryRankService.GetOrder(current);
        return a < 999 && b < 999 ? a == b : Normalize(MilitaryRankService.ShortName(source)) == Normalize(MilitaryRankService.ShortName(current));
    }

    private static bool IsOfficer(MilitaryRecord item) => MilitaryRankService.GetOrder(item.Rank) <= 10;
    private static string CleanOperationalName(string value) => Parenthetical().Replace(value.Split('/', 2)[0], " ").Trim();
    private static bool LooksLikePersonName(string value)
    {
        var normalized = Normalize(CleanOperationalName(value));
        return normalized.Length >= 2 && normalized is not ("NOME" or "NOME DE GUERRA" or "TOTAL" or "OUTROS")
            && normalized.Any(char.IsLetter) && !double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }
    private static string NormalizeName(string value) => Normalize(CleanOperationalName(value)).Replace(" ", string.Empty);
    private static string Normalize(string? value) => MilitaryRankService.Normalize(value).ToUpperInvariant();
    private static string Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index] ?? string.Empty : string.Empty;

    private static bool TryPositiveInt(string value, out int number)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) return true;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 && parsed <= int.MaxValue && Math.Abs(parsed - Math.Round(parsed)) < .000001)
        { number = (int)Math.Round(parsed); return true; }
        number = 0; return false;
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return -1;
        var value = 0;
        foreach (var character in reference) { if (!char.IsLetter(character)) break; value = value * 26 + char.ToUpperInvariant(character) - 'A' + 1; }
        return value - 1;
    }

    private static Column Col(uint number, double width) => new() { Min = number, Max = number, Width = width, CustomWidth = true };
    private static DocumentFormat.OpenXml.Spreadsheet.Row MakeRow(uint index, double height, params Cell[] cells) => new(cells) { RowIndex = index, Height = height, CustomHeight = true };
    private static Cell TextCell(string reference, string? value, uint style) => new() { CellReference = reference, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(value ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve }), StyleIndex = style };
    private static Cell NumberCell(string reference, int value, uint style) => new() { CellReference = reference, DataType = CellValues.Number, CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture)), StyleIndex = style };

    private static Stylesheet CreateStylesheet()
    {
        var fonts = new Fonts(
            new Font(new FontSize { Val = 10D }, new FontName { Val = "Aptos" }),
            new Font(new Bold(), new FontSize { Val = 16D }, new Color { Rgb = "FFFFFFFF" }, new FontName { Val = "Aptos Display" }),
            new Font(new Bold(), new FontSize { Val = 10D }, new Color { Rgb = "FFFFFFFF" }, new FontName { Val = "Aptos" }),
            new Font(new Bold(), new FontSize { Val = 10D }, new Color { Rgb = "FF0F172A" }, new FontName { Val = "Aptos" }));
        var fills = new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 }), Fill("FF16324F"), Fill("FFE8EEF5"), Fill("FFDCEAF7"), Fill("FFFFF4CC"));
        var edge = new Border(new LeftBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FFD5DDE5" } }, new RightBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FFD5DDE5" } }, new TopBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FFD5DDE5" } }, new BottomBorder { Style = BorderStyleValues.Thin, Color = new Color { Rgb = "FFD5DDE5" } }, new DiagonalBorder());
        var formats = new CellFormats(new CellFormat { FontId = 0U, FillId = 0U, BorderId = 0U }, Fmt(1, 2, 0, HorizontalAlignmentValues.Left, true), Fmt(3, 3, 0, HorizontalAlignmentValues.Left, false), Fmt(2, 2, 1, HorizontalAlignmentValues.Center, true), Fmt(3, 4, 1, HorizontalAlignmentValues.Center, false), Fmt(0, 3, 1, HorizontalAlignmentValues.Center, false), Fmt(0, 0, 1, HorizontalAlignmentValues.Left, false), Fmt(3, 5, 1, HorizontalAlignmentValues.Left, true));
        return new Stylesheet(fonts, fills, new Borders(new Border(), edge), new CellStyleFormats(new CellFormat()), formats);
    }
    private static Fill Fill(string rgb) => new(new PatternFill(new ForegroundColor { Rgb = rgb }, new BackgroundColor { Indexed = 64U }) { PatternType = PatternValues.Solid });
    private static CellFormat Fmt(uint font, uint fill, uint border, HorizontalAlignmentValues horizontal, bool wrap) => new() { FontId = font, FillId = fill, BorderId = border, ApplyFont = true, ApplyFill = true, ApplyBorder = border > 0, ApplyAlignment = true, Alignment = new Alignment { Horizontal = horizontal, Vertical = VerticalAlignmentValues.Center, WrapText = wrap } };

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Parenthetical();
    private sealed record SheetRows(string Name, List<List<string>> Rows);
    private sealed record TemplateRow(int RowIndex, int Id, int? Order);
    private sealed record ImportedName(string Name, string Rank, int RowIndex);
}

public sealed class MilitaryOrderImportResult
{
    public string SourceSheet { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public bool IsSigfurTemplate { get; set; }
    public int SourceEntries { get; set; }
    public int MatchedCount { get; set; }
    public int OfficersPreserved { get; set; }
    public int AppendedCount { get; set; }
    public int IgnoredIds { get; set; }
    public List<int> OrderedIds { get; set; } = [];
    public List<string> UnmatchedNames { get; } = [];
    public List<string> AmbiguousNames { get; } = [];
}
