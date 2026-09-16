using System.Security;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class MilitaryExportColumn
{
    public string Key { get; init; } = string.Empty;
    public string Header { get; init; } = string.Empty;
    public bool Essential { get; init; }
    public double Width { get; init; } = 18;
    public Func<MilitaryRecord, string> Value { get; init; } = _ => string.Empty;
}

public static class MilitaryExportService
{
    public static IReadOnlyList<MilitaryExportColumn> Columns { get; } =
    [
        new() { Key = "Rank", Header = "P/G", Essential = true, Width = 12, Value = m => m.ShortRank },
        new() { Key = "Name", Header = "Nome completo", Essential = true, Width = 38, Value = m => m.Name },
        new() { Key = "Cpf", Header = "CPF", Essential = true, Width = 17, Value = m => m.FormattedCpf },
        new() { Key = "Prec", Header = "PREC-CP", Essential = true, Width = 16, Value = m => m.PrecCp },
        new() { Key = "Idt", Header = "IDT", Essential = true, Width = 16, Value = m => m.MilitaryId },
        new() { Key = "FormationYear", Header = "Ano de formação", Width = 15, Value = m => m.FormationYear },
        new() { Key = "BirthDate", Header = "Nascimento", Width = 14, Value = m => m.BirthDate },
        new() { Key = "EnlistmentDate", Header = "Data de praça", Essential = true, Width = 14, Value = m => m.EnlistmentDate },
        new() { Key = "ServiceTime", Header = "Tempo de serviço", Essential = true, Width = 25, Value = m => m.ServiceTimeText },
        new() { Key = "Phone", Header = "Telefone", Width = 17, Value = m => m.Phone },
        new() { Key = "Email", Header = "E-mail", Width = 28, Value = m => m.Email },
        new() { Key = "Address", Header = "Endereço", Width = 42, Value = m => m.Address },
        new() { Key = "ZipCode", Header = "CEP", Width = 13, Value = m => m.ZipCode },
        new() { Key = "Bank", Header = "Banco", Width = 18, Value = m => m.Bank },
        new() { Key = "Agency", Header = "Agência", Width = 14, Value = m => m.Agency },
        new() { Key = "Account", Header = "Conta", Width = 18, Value = m => m.Account },
        new() { Key = "PreSchool", Header = "Recebe Pré-Escolar", Width = 20, Value = m => NormalizeYesNoDisplay(m.ReceivesPreSchool) },
        new() { Key = "PreSchoolValue", Header = "Valor Pré-Escolar", Width = 17, Value = m => m.PreSchoolValue },
        new() { Key = "Transport", Header = "SAT / Auxílio-Transporte", Width = 23, Value = m => m.TransportStatus },
        new() { Key = "TransportValue", Header = "Valor SAT/AT", Width = 14, Value = m => m.TransportAidValue },
        new() { Key = "Laranjeira", Header = "Laranjeira", Width = 14, Value = m => IsLaranjeira(m) ? "Sim" : "Não" },
        new() { Key = "Pnr", Header = "PNR", Width = 10, Value = m => m.PnrStatus },
        new() { Key = "Favorite", Header = "Favorito", Width = 10, Value = m => m.IsFavorite ? "Sim" : "Não" },
        new() { Key = "Attached", Header = "Adido/Encostado", Width = 18, Value = m => m.IsAttached ? "Sim" : "Não" },
        new() { Key = "Annotation", Header = "Anotação", Width = 36, Value = m => m.Annotation },
    ];

    public static async Task ExportAsync(
        string path,
        string format,
        IReadOnlyList<MilitaryRecord> rows,
        IReadOnlyCollection<string> selectedKeys,
        CancellationToken cancellationToken = default)
    {
        var columns = Columns.Where(x => selectedKeys.Contains(x.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (columns.Count == 0) throw new InvalidOperationException("Selecione ao menos uma coluna para exportar.");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        if (format.Contains("Excel", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            await ExportXlsxAsync(path, rows, columns, cancellationToken);
            return;
        }
        var separator = format.Contains("TXT", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) ? '\t' : ';';
        await ExportDelimitedAsync(path, rows, columns, separator, cancellationToken);
    }

    public static async Task ExportPersonnelRelationAsync(
        string path,
        IReadOnlyList<MilitaryRecord> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0) throw new InvalidOperationException("Não há militares para gerar a Relação Pessoal.");
        string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
        var spreadsheetRows = rows.Select((row, index) => new SpreadsheetRow
        {
            Cells =
            [
                new SpreadsheetCell { Text = (index + 1).ToString(CultureInfo.InvariantCulture) },
                new SpreadsheetCell { Text = Digits(row.PrecCp) },
                new SpreadsheetCell { Text = PersonnelRelationRank(row.Rank) },
                new SpreadsheetCell
                {
                    Runs = NameHighlightHelper.BuildSegments((row.Name ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR")), (row.WarName ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR")))
                        .Select(x => new SpreadsheetRun { Text = x.Text, Bold = x.IsBold })
                        .ToList()
                },
                new SpreadsheetCell { Text = Digits(row.Cpf) }
            ]
        }).ToList();

        await SpreadsheetService.WriteXlsxAsync(
            path,
            "Relação",
            ["NR ORDEM", "PREC-CP", "P/G", "NOME COMPLETO", "CPF"],
            [11, 16, 16, 46, 18],
            spreadsheetRows,
            cancellationToken);
    }


    public static async Task ExportBenefitsReportAsync(
        string path,
        IReadOnlyList<MilitaryRecord> rows,
        CancellationToken cancellationToken = default)
    {
        var selected = rows
            .Where(x => ReceivesPreSchool(x) || IsLaranjeira(x) || PaysJudicialPension(x) || LivesInPnr(x))
            .GroupBy(UniqueMilitaryKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (selected.Count == 0)
            throw new InvalidOperationException("Não há militares visíveis/selecionados com Pré-Escolar, Laranjeira, Pensão Judicial ou PNR.");

        var headers = new[]
        {
            "NR", "P/G", "NOME COMPLETO", "CPF", "PREC-CP",
            "PRÉ-ESCOLAR", "VALOR PRÉ-ESCOLAR", "LARANJEIRA",
            "PENSÃO JUDICIAL", "VALOR PENSÃO", "PNR", "RUA / ENDEREÇO DO PNR", "OBSERVAÇÃO"
        };
        var widths = new[] { 7d, 15d, 48d, 17d, 16d, 16d, 18d, 14d, 18d, 16d, 10d, 44d, 48d };
        var spreadsheetRows = new List<SpreadsheetRow>();
        var index = 1;
        foreach (var row in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            spreadsheetRows.Add(new SpreadsheetRow
            {
                Cells =
                [
                    new SpreadsheetCell { Text = (index++).ToString(CultureInfo.InvariantCulture) },
                    new SpreadsheetCell { Text = row.ShortRank },
                    new SpreadsheetCell
                    {
                        Runs = NameHighlightHelper.BuildSegments(
                                (row.Name ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR")),
                                (row.WarName ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR")))
                            .Select(x => new SpreadsheetRun { Text = x.Text, Bold = x.IsBold })
                            .ToList()
                    },
                    new SpreadsheetCell { Text = row.FormattedCpf },
                    new SpreadsheetCell { Text = row.PrecCp },
                    new SpreadsheetCell { Text = ReceivesPreSchool(row) ? "Sim" : "Não" },
                    new SpreadsheetCell { Text = ReceivesPreSchool(row) ? MoneyText(row.PreSchoolValue) : "—" },
                    new SpreadsheetCell { Text = IsLaranjeira(row) ? "Sim" : "Não" },
                    new SpreadsheetCell { Text = PaysJudicialPension(row) ? "Sim" : "Não" },
                    new SpreadsheetCell { Text = PaysJudicialPension(row) ? MoneyText(row.AlimonyValue) : "—" },
                    new SpreadsheetCell { Text = LivesInPnr(row) ? "Sim" : "Não" },
                    new SpreadsheetCell { Text = LivesInPnr(row) ? PnrStreetOrAddress(row) : "—" },
                    new SpreadsheetCell { Text = BuildSpecialRelationObservation(row) }
                ]
            });
        }

        await SpreadsheetService.WriteXlsxAsync(path, "Relação", headers, widths, spreadsheetRows, cancellationToken);
    }

    private static bool ReceivesSat(MilitaryRecord record)
        => !record.IsAttached && MilitaryRecord.IsYes(record.ReceivesTransportAid);

    private static bool ReceivesPreSchool(MilitaryRecord record)
        => MilitaryRecord.IsYes(record.ReceivesPreSchool);

    public static bool IsLaranjeira(MilitaryRecord record)
    {
        if (record.IsOrange) return true;
        var color = (record.CustomColor ?? string.Empty).Trim().ToUpperInvariant();
        return color.Contains("FFCC80", StringComparison.OrdinalIgnoreCase)
            || color.Contains("FFB74D", StringComparison.OrdinalIgnoreCase)
            || color.Contains("ORANGE", StringComparison.OrdinalIgnoreCase)
            || color.Contains("LARANJA", StringComparison.OrdinalIgnoreCase)
            || color.Contains("LARANJEIRA", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeYesNoDisplay(string? value)
        => MilitaryRecord.IsYes(value) ? "Sim" : "Não";

    private static string BuildBenefitsObservation(MilitaryRecord record)
    {
        var parts = new List<string>();
        if (ReceivesSat(record)) parts.Add("Recebe SAT/Auxílio-Transporte");
        if (ReceivesPreSchool(record)) parts.Add("Recebe Pré-Escolar");
        if (IsLaranjeira(record)) parts.Add("Laranjeira");
        if (PaysJudicialPension(record)) parts.Add("Paga Pensão Judicial");
        if (LivesInPnr(record)) parts.Add("Mora em PNR");
        if (record.IsAttached) parts.Add("Adido/Encostado");
        if (!string.IsNullOrWhiteSpace(record.Annotation)) parts.Add(record.Annotation.Trim());
        return string.Join("; ", parts);
    }

    private static string BuildSpecialRelationObservation(MilitaryRecord record)
    {
        var parts = new List<string>();
        if (ReceivesPreSchool(record)) parts.Add($"Pré-Escolar: {MoneyText(record.PreSchoolValue)}");
        if (IsLaranjeira(record)) parts.Add("Laranjeira");
        if (PaysJudicialPension(record)) parts.Add(string.IsNullOrWhiteSpace(record.AlimonyValue) ? "Pensão Judicial" : $"Pensão Judicial: {MoneyText(record.AlimonyValue)}");
        if (LivesInPnr(record)) parts.Add($"PNR: {PnrStreetOrAddress(record)}");
        if (!string.IsNullOrWhiteSpace(record.Annotation)) parts.Add(record.Annotation.Trim());
        return string.Join("; ", parts);
    }

    private static bool PaysJudicialPension(MilitaryRecord record)
        => MilitaryRecord.IsYes(record.Alimony) || ParseMoney(record.AlimonyValue) > 0;

    private static bool LivesInPnr(MilitaryRecord record)
        => MilitaryRecord.IsYes(record.HasPnr);

    private static string UniqueMilitaryKey(MilitaryRecord record)
    {
        var cpf = DigitsOnly(record.Cpf);
        if (!string.IsNullOrWhiteSpace(cpf)) return "CPF:" + cpf;
        var prec = DigitsOnly(record.PrecCp);
        if (!string.IsNullOrWhiteSpace(prec)) return "PREC:" + prec;
        var idt = DigitsOnly(record.MilitaryId);
        if (!string.IsNullOrWhiteSpace(idt)) return "IDT:" + idt;
        return $"NOME:{record.Rank}|{record.Name}|{record.WarName}".ToUpperInvariant();
    }

    private static string PnrStreetOrAddress(MilitaryRecord record)
    {
        var address = (record.Address ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(address)) return "Endereço não informado";

        // Mantém o nome da rua claro para a conferência, removendo complementos muito longos quando possível.
        var normalized = System.Text.RegularExpressions.Regex.Replace(address, @"\s+", " ").Trim();
        var parts = normalized.Split(';', ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return normalized;
        var street = parts.FirstOrDefault(x => ContainsStreetHint(x)) ?? parts[0];
        return string.IsNullOrWhiteSpace(street) ? normalized : street;
    }

    private static bool ContainsStreetHint(string value)
    {
        var text = RemoveDiacritics(value).ToUpperInvariant();
        return text.Contains("RUA", StringComparison.Ordinal)
            || text.Contains("AVENIDA", StringComparison.Ordinal)
            || text.Contains("AV ", StringComparison.Ordinal)
            || text.StartsWith("AV.", StringComparison.Ordinal)
            || text.Contains("TRAVESSA", StringComparison.Ordinal)
            || text.Contains("ALAMEDA", StringComparison.Ordinal)
            || text.Contains("PRACA", StringComparison.Ordinal)
            || text.Contains("RODOVIA", StringComparison.Ordinal);
    }

    private static string MoneyText(string? value)
    {
        var amount = ParseMoney(value);
        return amount <= 0 ? "—" : amount.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    }

    private static decimal ParseMoney(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        text = text.Replace("R$", "", StringComparison.OrdinalIgnoreCase).Trim();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^0-9,.-]", "");
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        if (text.Contains(',', StringComparison.Ordinal))
        {
            text = text.Replace(".", string.Empty).Replace(',', '.');
            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var br) ? br : 0m;
        }
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var inv) ? inv : 0m;
    }

    private static string DigitsOnly(string? value)
        => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    private static string RemoveDiacritics(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) builder.Append(ch);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static void ValidateXlsx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var required in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml" })
            if (archive.GetEntry(required) is null) throw new InvalidDataException("A planilha gerada ficou incompleta: " + required);
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!;
        using var reader = new StreamReader(sheet.Open(), Encoding.UTF8);
        var xml = reader.ReadToEnd();
        if (!xml.Contains("NOME COMPLETO", StringComparison.Ordinal) || !xml.Contains("NR ORDEM", StringComparison.Ordinal))
            throw new InvalidDataException("A planilha foi criada, mas o conteúdo da Relação Pessoal não foi confirmado.");
    }

    private static string BuildPersonnelRelationSheetXml(IReadOnlyList<MilitaryRecord> rows)
    {
        string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        var lastRow = Math.Max(1, rows.Count + 1);
        sb.Append($"<dimension ref=\"A1:E{lastRow}\"/>");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("<cols><col min=\"1\" max=\"1\" width=\"11\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"16\" customWidth=\"1\"/><col min=\"3\" max=\"3\" width=\"16\" customWidth=\"1\"/><col min=\"4\" max=\"4\" width=\"46\" customWidth=\"1\"/><col min=\"5\" max=\"5\" width=\"18\" customWidth=\"1\"/></cols><sheetData>");
        sb.Append("<row r=\"1\" ht=\"24\" customHeight=\"1\">");
        var headers = new[] { "NR ORDEM", "PREC-CP", "P/G", "NOME COMPLETO", "CPF" };
        for (var index = 0; index < headers.Length; index++) AppendCell(sb, CellRef(index + 1, 1), headers[index], 1);
        sb.Append("</row>");

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var excelRow = index + 2;
            var style = index % 2 == 0 ? 2 : 3;
            sb.Append($"<row r=\"{excelRow}\" ht=\"21\" customHeight=\"1\">");
            AppendCell(sb, CellRef(1, excelRow), (index + 1).ToString(CultureInfo.InvariantCulture), style);
            AppendCell(sb, CellRef(2, excelRow), Digits(row.PrecCp), style);
            AppendCell(sb, CellRef(3, excelRow), PersonnelRelationRank(row.Rank), style);
            AppendPersonnelRichNameCell(sb, CellRef(4, excelRow), row, style);
            AppendCell(sb, CellRef(5, excelRow), Digits(row.Cpf), style);
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        sb.Append($"<autoFilter ref=\"A1:E{lastRow}\"/>");
        sb.Append("<pageSetup orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/><pageMargins left=\"0.25\" right=\"0.25\" top=\"0.45\" bottom=\"0.45\" header=\"0.2\" footer=\"0.2\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static string PersonnelRelationRank(string? rank)
    {
        return MilitaryRankService.Canonicalize(rank) switch
        {
            "Capitão" => "CAP",
            "1º Tenente" => "1º TEN",
            "2º Tenente" => "2º TEN",
            "Aspirante" => "ASP",
            "Subtenente" => "ST",
            "1º Sargento" => "1º SGT",
            "2º Sargento" => "2º SGT",
            "3º Sargento" => "3º SGT",
            "Cabo Efetivo Profissional" => "Cb EP",
            "Soldado Efetivo Profissional" => "Sd EP",
            "Soldado Efetivo Variável" => "Sd EV",
            var other => string.IsNullOrWhiteSpace(other) ? "—" : other.ToUpperInvariant()
        };
    }

    private static void AppendPersonnelRichNameCell(StringBuilder sb, string reference, MilitaryRecord row, int style)
    {
        var fullName = (row.Name ?? string.Empty).ToUpperInvariant();
        var warName = (row.WarName ?? string.Empty).ToUpperInvariant();
        sb.Append($"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is>");
        foreach (var segment in NameHighlightHelper.BuildSegments(fullName, warName))
        {
            var escaped = SecurityElement.Escape(segment.Text) ?? string.Empty;
            sb.Append("<r><rPr><rFont val=\"Calibri\"/><sz val=\"11\"/>");
            if (segment.IsBold) sb.Append("<b/>");
            sb.Append("</rPr>");
            sb.Append($"<t xml:space=\"preserve\">{escaped}</t></r>");
        }
        sb.Append("</is></c>");
    }

    private static async Task ExportDelimitedAsync(
        string path,
        IReadOnlyList<MilitaryRecord> rows,
        IReadOnlyList<MilitaryExportColumn> columns,
        char separator,
        CancellationToken cancellationToken)
    {
        static string Escape(string value, char separator)
        {
            value ??= string.Empty;
            if (value.Contains(separator) || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
                return '"' + value.Replace("\"", "\"\"") + '"';
            return value;
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(separator, columns.Select(x => Escape(x.Header, separator))));
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sb.AppendLine(string.Join(separator, columns.Select(x => Escape(x.Value(row), separator))));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    private static async Task ExportXlsxAsync(
        string path,
        IReadOnlyList<MilitaryRecord> rows,
        IReadOnlyList<MilitaryExportColumn> columns,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path)) File.Delete(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        await WriteEntryAsync(archive, "[Content_Types].xml", ContentTypesXml, cancellationToken);
        await WriteEntryAsync(archive, "_rels/.rels", RootRelsXml, cancellationToken);
        await WriteEntryAsync(archive, "xl/workbook.xml", WorkbookXml, cancellationToken);
        await WriteEntryAsync(archive, "xl/_rels/workbook.xml.rels", WorkbookRelsXml, cancellationToken);
        await WriteEntryAsync(archive, "xl/styles.xml", StylesXml, cancellationToken);
        await WriteEntryAsync(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(rows, columns), cancellationToken);
        await WriteEntryAsync(archive, "docProps/core.xml", CoreXml, cancellationToken);
        await WriteEntryAsync(archive, "docProps/app.xml", AppXml, cancellationToken);
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static string BuildSheetXml(IReadOnlyList<MilitaryRecord> rows, IReadOnlyList<MilitaryExportColumn> columns)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        var lastColumn = ColumnName(columns.Count);
        var lastRow = Math.Max(1, rows.Count + 1);
        sb.Append($"<dimension ref=\"A1:{lastColumn}{lastRow}\"/>");
        sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sb.Append("<cols>");
        for (var i = 0; i < columns.Count; i++)
            sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{columns[i].Width.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");

        sb.Append("<row r=\"1\" ht=\"24\" customHeight=\"1\">");
        for (var i = 0; i < columns.Count; i++)
            AppendCell(sb, CellRef(i + 1, 1), columns[i].Header, 1);
        sb.Append("</row>");

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var excelRow = rowIndex + 2;
            var row = rows[rowIndex];
            sb.Append($"<row r=\"{excelRow}\" ht=\"20\" customHeight=\"1\">");
            for (var colIndex = 0; colIndex < columns.Count; colIndex++)
            {
                var column = columns[colIndex];
                var reference = CellRef(colIndex + 1, excelRow);
                if (column.Key == "Name") AppendRichNameCell(sb, reference, row, rowIndex % 2 == 0 ? 2 : 3);
                else AppendCell(sb, reference, column.Value(row), rowIndex % 2 == 0 ? 2 : 3);
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        sb.Append($"<autoFilter ref=\"A1:{lastColumn}{lastRow}\"/>");
        sb.Append("<pageMargins left=\"0.35\" right=\"0.35\" top=\"0.5\" bottom=\"0.5\" header=\"0.2\" footer=\"0.2\"/>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static void AppendCell(StringBuilder sb, string reference, string value, int style)
    {
        var escaped = SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
        sb.Append($"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{escaped}</t></is></c>");
    }

    private static void AppendRichNameCell(StringBuilder sb, string reference, MilitaryRecord row, int style)
    {
        sb.Append($"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is>");
        foreach (var segment in NameHighlightHelper.BuildSegments(row.Name, row.WarName))
        {
            var escaped = SecurityElement.Escape(segment.Text) ?? string.Empty;
            sb.Append("<r><rPr><rFont val=\"Calibri\"/><sz val=\"11\"/>");
            if (segment.IsBold) sb.Append("<b/>");
            sb.Append("</rPr>");
            sb.Append($"<t xml:space=\"preserve\">{escaped}</t></r>");
        }
        sb.Append("</is></c>");
    }

    private static string CellRef(int column, int row) => ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);
    private static string ColumnName(int number)
    {
        var name = string.Empty;
        while (number > 0)
        {
            number--;
            name = (char)('A' + number % 26) + name;
            number /= 26;
        }
        return name;
    }

    private const string ContentTypesXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
  <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
  <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
</Types>
""";

    private const string RootRelsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
  <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
</Relationships>
""";

    private const string PersonnelRelationWorkbookXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets><sheet name="Relação" sheetId="1" r:id="rId1"/></sheets>
</workbook>
""";

    private const string WorkbookXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets><sheet name="Militares" sheetId="1" r:id="rId1"/></sheets>
</workbook>
""";

    private const string WorkbookRelsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
""";

    private const string StylesXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <fonts count="2">
    <font><sz val="11"/><name val="Calibri"/><family val="2"/></font>
    <font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/><family val="2"/></font>
  </fonts>
  <fills count="4">
    <fill><patternFill patternType="none"/></fill>
    <fill><patternFill patternType="gray125"/></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FF1F4E79"/><bgColor indexed="64"/></patternFill></fill>
    <fill><patternFill patternType="solid"><fgColor rgb="FFF4F8FC"/><bgColor indexed="64"/></patternFill></fill>
  </fills>
  <borders count="2">
    <border><left/><right/><top/><bottom/><diagonal/></border>
    <border><left style="thin"><color rgb="FFD9E2F3"/></left><right style="thin"><color rgb="FFD9E2F3"/></right><top style="thin"><color rgb="FFD9E2F3"/></top><bottom style="thin"><color rgb="FFD9E2F3"/></bottom><diagonal/></border>
  </borders>
  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
  <cellXfs count="4">
    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
    <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf>
    <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
    <xf numFmtId="0" fontId="0" fillId="3" borderId="1" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf>
  </cellXfs>
  <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
</styleSheet>
""";

    private static readonly string CoreXml = $"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <dc:title>Relação de militares — SIGFUR</dc:title><dc:creator>SIGFUR</dc:creator><cp:lastModifiedBy>SIGFUR</cp:lastModifiedBy>
  <dcterms:created xsi:type="dcterms:W3CDTF">{DateTime.UtcNow:O}</dcterms:created>
</cp:coreProperties>
""";

    private const string AppXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes"><Application>SIGFUR</Application></Properties>
""";
}
