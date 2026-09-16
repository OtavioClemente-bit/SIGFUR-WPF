using System.Runtime.InteropServices;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class ExercisePreviousExcelService
{
    private readonly ExercisePreviousAssetsService _assets;
    private readonly ExercisePreviousRepository _repository;

    public ExercisePreviousExcelService(ExercisePreviousAssetsService assets, ExercisePreviousRepository repository)
    {
        _assets = assets;
        _repository = repository;
    }

    public Task<List<ExercisePreviousCode>> ReadDefaultCodesAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            _assets.EnsureInstalled();
            return ReadDefaultCodesWithoutExcel(_assets.TemplateWorkbook);
        }, ct);

    private static List<ExercisePreviousCode> ReadDefaultCodesWithoutExcel(string workbookPath)
    {
        var fallback = Enumerable.Range(1, 17).Select(i => new ExercisePreviousCode { Order = i }).ToList();
        try
        {
            using var archive = ZipFile.OpenRead(workbookPath);
            var workbookEntry = archive.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("workbook.xml ausente.");
            var relationsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("relações do workbook ausentes.");
            var workbook = LoadXml(workbookEntry);
            var relations = LoadXml(relationsEntry);
            System.Xml.Linq.XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            System.Xml.Linq.XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            System.Xml.Linq.XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
            var sheetNode = workbook.Descendants(main + "sheet").FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), "Códigos Lançamento", StringComparison.OrdinalIgnoreCase));
            var rid = (string?)sheetNode?.Attribute(rel + "id");
            var target = relations.Descendants(packageRel + "Relationship").FirstOrDefault(x => string.Equals((string?)x.Attribute("Id"), rid, StringComparison.OrdinalIgnoreCase))?.Attribute("Target")?.Value;
            if (string.IsNullOrWhiteSpace(target)) return fallback;
            var sheetPath = target.StartsWith("/", StringComparison.Ordinal) ? target.TrimStart('/') : "xl/" + target.TrimStart('/');
            var sheetEntry = archive.GetEntry(sheetPath.Replace('\\', '/'));
            if (sheetEntry is null) return fallback;
            var sharedStrings = ReadSharedStrings(archive, main);
            var sheet = LoadXml(sheetEntry);
            var cells = sheet.Descendants(main + "c").ToDictionary(x => (string?)x.Attribute("r") ?? string.Empty, x => ReadCellText(x, sharedStrings, main), StringComparer.OrdinalIgnoreCase);
            var result = new List<ExercisePreviousCode>();
            for (var i = 0; i < 17; i++)
            {
                var row = i + 3;
                cells.TryGetValue($"A{row}", out var description);
                cells.TryGetValue($"B{row}", out var type);
                type = type?.Trim();
                if (type is not ("Receita" or "Despesa")) type = "-";
                result.Add(new ExercisePreviousCode { Order = i + 1, Description = (description ?? string.Empty).Trim(), Type = type });
            }
            return result;
        }
        catch { return fallback; }

        static System.Xml.Linq.XDocument LoadXml(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            return System.Xml.Linq.XDocument.Load(stream, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        }

        static List<string> ReadSharedStrings(ZipArchive archive, System.Xml.Linq.XNamespace main)
        {
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry is null) return [];
            var document = LoadXml(entry);
            return document.Descendants(main + "si")
                .Select(si => string.Concat(si.Descendants(main + "t").Select(t => t.Value)))
                .ToList();
        }

        static string ReadCellText(System.Xml.Linq.XElement cell, IReadOnlyList<string> shared, System.Xml.Linq.XNamespace main)
        {
            var type = (string?)cell.Attribute("t") ?? string.Empty;
            if (type == "inlineStr") return string.Concat(cell.Descendants(main + "t").Select(x => x.Value));
            var value = cell.Element(main + "v")?.Value ?? string.Empty;
            if (type == "s" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count) return shared[index];
            return value;
        }
    }

    public Task<int> ImportIpcaFromTemplateAsync(CancellationToken ct = default)
    {
        _assets.EnsureInstalled();
        return ImportIpcaFromWorkbookAsync(_assets.TemplateWorkbook, ct);
    }

    public async Task<int> EnsureIpcaFromTemplateAsync(CancellationToken ct = default)
    {
        _assets.EnsureInstalled();
        var signatureFile = Path.Combine(_assets.RootDirectory, "ipca_template.sha256");
        var signature = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(_assets.TemplateWorkbook, ct)));
        var stored = File.Exists(signatureFile) ? (await File.ReadAllTextAsync(signatureFile, ct)).Trim() : string.Empty;
        var maxCompetence = await _repository.GetMaxIpcaCompetenceAsync(ct);
        if (string.Equals(signature, stored, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(maxCompetence))
            return 0;

        var count = await ImportIpcaFromWorkbookAsync(_assets.TemplateWorkbook, ct);
        await File.WriteAllTextAsync(signatureFile, signature, new UTF8Encoding(false), ct);
        return count;
    }

    public Task<int> ImportIpcaFromWorkbookAsync(string workbookPath, CancellationToken ct = default)
        => RunStaAsync(() =>
        {
            if (string.IsNullOrWhiteSpace(workbookPath) || !File.Exists(workbookPath))
                throw new FileNotFoundException("Planilha IPCA-E não encontrada.", workbookPath);

            var rows = WithExcel(workbookPath, readOnly: true, visible: false, (app, workbook) =>
            {
                // O fator acumulado pode depender de fórmulas. Recalcula antes de ler,
                // evitando importar valores antigos armazenados no arquivo.
                try { app.CalculateFull(); } catch { }
                dynamic sheet = FindWorksheet(workbook, "Cálculo do Acumulado");
                try
                {
                    object? maxRaw = sheet.UsedRange.Rows.Count;
                    int max = Convert.ToInt32(maxRaw, CultureInfo.InvariantCulture);
                    var values = new List<(string Competence, double Percentage, double Factor)>();
                    for (var row = 3; row <= max; row++)
                    {
                        object? monthYearRaw = sheet.Cells[row, 1].Value2;
                        string monthYear = Convert.ToString(monthYearRaw, CultureInfo.InvariantCulture) ?? string.Empty;
                        string competence = ParsePortugueseMonthYear(monthYear);
                        if (string.IsNullOrWhiteSpace(competence)) continue;
                        object? factorRaw = sheet.Cells[row, 4].Value2;
                        double factor;
                        if (!TryDouble(factorRaw, out factor)) continue;

                        object? percentageRaw = sheet.Cells[row, 2].Value2;
                        if (!TryDouble(percentageRaw, out var percentage)) continue;
                        values.Add((competence, percentage, factor));
                    }
                    return values;
                }
                finally
                {
                    Release(sheet);
                }
            });
            if (rows.Count == 0) throw new InvalidDataException("Nenhum índice IPCA-E válido foi encontrado na planilha.");
            _repository.ReplaceIpcaAsync(rows, ct).GetAwaiter().GetResult();
            return rows.Count;
        }, ct);

    public Task<string> GenerateWorkbookAsync(ExercisePreviousProcess process, bool openAfterGenerate = true, CancellationToken ct = default)
        => RunStaAsync(() =>
        {
            _assets.EnsureInstalled();
            var output = _assets.CreateWorkbookOutputPath(process);
            File.Copy(_assets.TemplateWorkbook, output, true);

            // O modelo oficial vem com praticamente todas as abas protegidas.
            // O módulo Python conseguia escrever porque o openpyxl ignora a proteção
            // de interface; já o Excel COM bloqueia a alteração e exibe a mensagem
            // “A célula ou gráfico ... está em uma planilha protegida”.
            // Removemos a proteção apenas da CÓPIA gerada para o processo, preservando
            // integralmente VBA, fórmulas, objetos, estilos e o modelo original.
            RemoveSheetProtection(output);

            WithExcel(output, readOnly: false, visible: false, (app, workbook) =>
            {
                try { app.AutomationSecurity = 3; } catch { }
                try { app.Calculation = -4135; } catch { } // xlCalculationManual
                FillWorkbook(workbook, process);
                try { app.Calculation = -4105; } catch { } // xlCalculationAutomatic
                try { app.CalculateFullRebuild(); } catch { }
                WaitForCalculation(app);
                ValidateFilledWorkbook(workbook, process);
                workbook.Save();
                return 0;
            });
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new IOException("O Excel não concluiu a gravação da planilha EA.");
            if (openAfterGenerate) Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
            return output;
        }, ct);

    public Task<string> ExportPdfAsync(string workbookPath, ExercisePreviousProcess process, CancellationToken ct = default)
        => RunStaAsync(() =>
        {
            if (!File.Exists(workbookPath)) throw new FileNotFoundException("Planilha EA não encontrada.", workbookPath);
            var pdf = Path.ChangeExtension(workbookPath, ".pdf");
            var temporaryPdf = Path.Combine(Path.GetTempPath(), $"SIGFUR_EA_{Guid.NewGuid():N}.pdf");
            try
            {
                WithExcel(workbookPath, readOnly: true, visible: false, (app, workbook) =>
                {
                    var sheets = GetSheetsToPrint(workbook, process);
                    if (sheets.Count == 0) throw new InvalidOperationException("Nenhuma aba obrigatória foi encontrada no XLSM.");
                    SelectSheets(workbook, sheets);
                    app.ActiveSheet.ExportAsFixedFormat(0, temporaryPdf, 0, true, false);
                    return 0;
                });

                if (!File.Exists(temporaryPdf) || new FileInfo(temporaryPdf).Length == 0)
                    throw new IOException("O Microsoft Excel não produziu um PDF válido para o processo EA.");

                File.Copy(temporaryPdf, pdf, true);
                if (!File.Exists(pdf) || new FileInfo(pdf).Length == 0)
                    throw new IOException("O PDF foi criado, mas não pôde ser copiado para a pasta final do processo.");

                Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true });
                return pdf;
            }
            finally
            {
                try { if (File.Exists(temporaryPdf)) File.Delete(temporaryPdf); } catch { }
            }
        }, ct);

    public Task PrintAsync(string workbookPath, ExercisePreviousProcess process, CancellationToken ct = default)
        => RunStaAsync(() =>
        {
            if (!File.Exists(workbookPath)) throw new FileNotFoundException("Planilha EA não encontrada.", workbookPath);
            WithExcel(workbookPath, readOnly: true, visible: false, (app, workbook) =>
            {
                var sheets = GetSheetsToPrint(workbook, process);
                if (sheets.Count == 0) throw new InvalidOperationException("Nenhuma aba obrigatória foi encontrada no XLSM.");
                SelectSheets(workbook, sheets);
                app.ActiveSheet.PrintOut();
                return 0;
            });
            return 0;
        }, ct);

    private static void FillWorkbook(dynamic workbook, ExercisePreviousProcess p)
    {
        var templateMaxYear = DetectTemplateMaxYear(workbook);
        if (templateMaxYear < 2006)
            throw new InvalidDataException("O modelo de Exercício Anterior não possui abas anuais válidas.");

        var unsupportedYear = p.Entries
            .Where(x => x.Received != 0m || x.Due != 0m)
            .Select(x => x.Year)
            .FirstOrDefault(x => x < 2006 || x > templateMaxYear);
        if (unsupportedYear != 0)
            throw new InvalidOperationException($"Há lançamento em {unsupportedYear}, mas o modelo atual aceita competências de 2006 a {templateMaxYear}. Atualize o modelo antes de gerar o relatório.");

        RepairAnnualFormulas(workbook, templateMaxYear);

        dynamic info = workbook.Worksheets["Informações"];
        var cells = new Dictionary<string, object?>
        {
            ["B4"] = p.OrganizationName, ["B5"] = p.MilitaryRegion, ["B6"] = p.ManagementUnit, ["B7"] = p.Codom,
            ["B8"] = p.OdNameRank, ["B9"] = p.OdFunction, ["B10"] = p.PersonnelChiefNameRank,
            ["B11"] = p.PersonnelChiefFunction, ["B12"] = p.AdministrativeInspectorNameRank,
            ["B13"] = p.AdministrativeInspectorFunction, ["B14"] = p.CityState,
            ["B15"] = p.ProcessNumber, ["B16"] = p.ProcessYear, ["A31"] = p.RequestDateInWords,
            ["B20"] = p.Rank, ["B21"] = p.FullName, ["B22"] = p.PrecCp, ["B23"] = p.Identity,
            ["B24"] = p.Cpf, ["B25"] = p.Bank, ["B26"] = p.Agency, ["B27"] = p.Account,
            ["H30"] = FormatDateBr(p.PeriodStart), ["H31"] = FormatDateBr(p.PeriodEnd),
            ["E4"] = FormatDateBr(p.RequestDate), ["E5"] = ExercisePreviousRepository.ExtractBulletinNumber(p.BulletinNumber),
            ["E6"] = FormatDateBr(p.BulletinDate), ["A29"] = p.DebtType, ["A33"] = p.Situation
        };
        foreach (var pair in cells) info.Range[pair.Key].Value2 = pair.Value ?? string.Empty;
        FillPeriodsPerYear(info, p.PeriodStart, p.PeriodEnd, templateMaxYear);
        Release(info);

        dynamic codeSheet = workbook.Worksheets["Códigos Lançamento"];
        for (var i = 1; i <= 17; i++)
        {
            var code = p.Codes.FirstOrDefault(x => x.Order == i);
            codeSheet.Cells[i + 2, 1].Value2 = code?.Description ?? string.Empty;
            codeSheet.Cells[i + 2, 2].Value2 = code?.Type ?? "-";
        }
        Release(codeSheet);

        FillLaunchSheet(workbook, "Contracheque - F Financeira", p.Entries, true, templateMaxYear);
        FillLaunchSheet(workbook, "Lançar Valor Devido", p.Entries, false, templateMaxYear);

        if (SheetExists(workbook, "Solicitação Verso"))
        {
            dynamic verso = workbook.Worksheets["Solicitação Verso"];
            verso.Range["D16"].Value2 = p.RightMaterializationDocument;
            verso.Range["E16"].Value2 = ExpandBulletinGuide(p.BulletinThatRecorded, p);
            verso.Range["F16"].Value2 = p.NonPaymentExplanation;
            Release(verso);
        }
    }

    private static void FillPeriodsPerYear(dynamic info, string startRaw, string endRaw, int templateMaxYear)
    {
        // Cada linha de E8 em diante alimenta o relatório anual correspondente.
        // O limite precisa acompanhar as abas do próprio modelo (hoje ele já possui
        // 2026/2027/2028); manter 2025 fixo deixava os relatórios novos sem período.
        var lastRow = 8 + Math.Max(0, templateMaxYear - 2006);
        for (var row = 8; row <= lastRow; row++) info.Cells[row, 5].Value2 = string.Empty;
        if (!TryDate(startRaw, out var start) || !TryDate(endRaw, out var end)) return;
        if (end < start) (start, end) = (end, start);
        var months = ExercisePreviousDefaults.Months;
        static string TwoDigitYear(DateTime value) => (value.Year % 100).ToString("00", CultureInfo.InvariantCulture);
        for (var year = Math.Max(2006, start.Year); year <= Math.Min(templateMaxYear, end.Year); year++)
        {
            var yearStart = start.Year == year ? start : new DateTime(year, 1, 1);
            var yearEnd = end.Year == year ? end : new DateTime(year, 12, 31);
            var text = $"{yearStart.Day:00} {months[yearStart.Month - 1]} {TwoDigitYear(yearStart)} a {yearEnd.Day:00} {months[yearEnd.Month - 1]} {TwoDigitYear(yearEnd)}";
            info.Cells[8 + (year - 2006), 5].Value2 = text;
        }
    }

    private static void RepairAnnualFormulas(dynamic workbook, int templateMaxYear)
    {
        // O modelo distribuído já possui abas até 2028, porém as cópias de 2026+
        // conservaram algumas referências de 2025 e o verso de 2028 apontava para 2027.
        // Regrava apenas as referências anuais previsíveis na cópia de saída.
        for (var year = 2006; year <= templateMaxYear; year++)
        {
            var yy = (year % 100).ToString("00", CultureInfo.InvariantCulture);
            var periodRow = 8 + (year - 2006);
            foreach (var suffix in new[] { "Recebido", "Devido" })
            {
                var name = $"{yy}-{suffix}";
                if (!SheetExists(workbook, name)) continue;
                dynamic? annual = null;
                try
                {
                    annual = workbook.Worksheets[name];
                    annual.Range["C5"].Formula = $"='Informações'!E{periodRow}";
                }
                finally { Release(annual); }
            }

            var versoName = $"Solicitação {yy} Verso";
            if (!SheetExists(workbook, versoName)) continue;
            dynamic? verso = null;
            try
            {
                verso = workbook.Worksheets[versoName];
                verso.Range["A7"].Value2 = year;
                for (var codeOrder = 1; codeOrder <= 17; codeOrder++)
                {
                    var targetRow = 14 + codeOrder;
                    var sourceRow = 5 + ((year - 2006) * 19) + codeOrder;
                    verso.Cells[targetRow, 4].Formula = $"=SUM('Lançar Valor Devido'!D{sourceRow}:O{sourceRow})";
                    verso.Cells[targetRow, 5].Formula = $"=SUM('Contracheque - F Financeira'!D{sourceRow}:O{sourceRow})";
                    verso.Cells[targetRow, 6].Formula = $"=D{targetRow}-E{targetRow}";
                    verso.Cells[targetRow, 7].Formula = $"=Valor_Líquido_Corrigido!O{sourceRow - 1}";
                }
            }
            finally { Release(verso); }
        }
    }

    private static void FillLaunchSheet(dynamic workbook, string sheetName, IEnumerable<ExercisePreviousEntry> entries, bool received, int templateMaxYear)
    {
        if (!SheetExists(workbook, sheetName)) return;
        dynamic sheet = workbook.Worksheets[sheetName];
        var endYear = Math.Min(templateMaxYear, DateTime.Today.Year);
        var values = entries
            .Where(x => x.CodeOrder is >= 1 and <= 17 && x.Month is >= 1 and <= 12 && x.Year is >= 2006)
            .GroupBy(x => (x.Year, x.Month, x.CodeOrder))
            .ToDictionary(x => x.Key, x => x.Last());

        // A implementação Python zerava toda a matriz antes de aplicar os lançamentos.
        // Repetimos a regra para impedir que valores de exemplo ou resíduos do modelo
        // permaneçam em competências sem lançamento no processo atual.
        for (var year = 2006; year <= endYear; year++)
        for (var codeOrder = 1; codeOrder <= 17; codeOrder++)
        for (var month = 1; month <= 12; month++)
        {
            var row = 5 + ((year - 2006) * 19) + codeOrder;
            var column = 3 + month; // D=4 para janeiro
            var value = values.TryGetValue((year, month, codeOrder), out var entry)
                ? received ? entry.Received : entry.Due
                : 0m;
            sheet.Cells[row, column].Value2 = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        Release(sheet);
    }

    private static List<string> GetSheetsToPrint(dynamic workbook, ExercisePreviousProcess process)
    {
        var result = new List<string>();
        foreach (var fixedSheet in new[] { "Solicitação", "Solicitação Verso", "CPEX", "Solicitação Recapitulação" })
            if (SheetExists(workbook, fixedSheet)) result.Add(fixedSheet);
        var maxYear = DetectTemplateMaxYear(workbook);
        var startYear = TryDate(process.PeriodStart, out var start) ? Math.Max(2018, start.Year) : 2018;
        // A versão Python imprime do ano inicial da dívida até o menor entre o ano atual
        // e o último ano suportado pelo modelo, independentemente da data final do período.
        var endYear = Math.Min(maxYear, DateTime.Today.Year);
        if (startYear > endYear) startYear = endYear;
        for (var year = startYear; year <= endYear; year++)
        {
            var yy = (year % 100).ToString("00", CultureInfo.InvariantCulture);
            foreach (var name in new[] { $"Solicitação {yy}", $"Solicitação {yy} Verso", $"{yy}-Recebido", $"{yy}-Devido" })
                if (SheetExists(workbook, name)) result.Add(name);
        }
        return result;
    }

    private static int DetectTemplateMaxYear(dynamic workbook)
    {
        var max = 2005;
        foreach (dynamic sheet in workbook.Worksheets)
        {
            try
            {
                object? nameRaw = sheet.Name;
                string name = Convert.ToString(nameRaw, CultureInfo.InvariantCulture) ?? string.Empty;
                var match = System.Text.RegularExpressions.Regex.Match(name, @"^Solicitação\s+(\d{2})$");
                if (match.Success) max = Math.Max(max, 2000 + int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
            finally { Release(sheet); }
        }
        return max;
    }

    private static void WaitForCalculation(dynamic app)
    {
        // Algumas máquinas devolvem o controle antes de a planilha pesada terminar
        // de recalcular. Salvar/fechar nesse intervalo mantém os resultados em branco.
        try { app.CalculateUntilAsyncQueriesDone(); } catch { }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                // xlDone = 0
                if (Convert.ToInt32(app.CalculationState, CultureInfo.InvariantCulture) == 0) return;
            }
            catch { return; }
            Thread.Sleep(50);
        }
        throw new TimeoutException("O Excel não concluiu o cálculo do relatório de Exercício Anterior em 30 segundos. Tente gerar novamente com outras planilhas do Excel fechadas.");
    }

    private static void ValidateFilledWorkbook(dynamic workbook, ExercisePreviousProcess process)
    {
        // Detecta antes de manter referências COM para abas específicas; a enumeração
        // libera os RCWs temporários e não pode invalidar uma aba já em uso abaixo.
        var templateMaxYear = DetectTemplateMaxYear(workbook);
        dynamic? info = null;
        dynamic? launch = null;
        dynamic? request = null;
        try
        {
            info = workbook.Worksheets["Informações"];
            ValidateCell(info, "B21", process.FullName, "nome completo");
            ValidateCell(info, "B24", process.Cpf, "CPF");
            ValidateCell(info, "B15", process.ProcessNumber, "número do processo");

            var entry = process.Entries.FirstOrDefault(x =>
                x.CodeOrder is >= 1 and <= 17 && x.Month is >= 1 and <= 12 && x.Year >= 2006 &&
                (x.Received != 0m || x.Due != 0m));
            if (entry is not null)
            {
                var received = entry.Received != 0m;
                launch = workbook.Worksheets[received ? "Contracheque - F Financeira" : "Lançar Valor Devido"];
                var row = 5 + ((entry.Year - 2006) * 19) + entry.CodeOrder;
                var column = 3 + entry.Month;
                var actual = Convert.ToDecimal(launch.Cells[row, column].Value2 ?? 0d, CultureInfo.InvariantCulture);
                var expected = received ? entry.Received : entry.Due;
                if (Math.Abs(actual - expected) > 0.005m)
                    throw new IOException($"O Excel não recebeu o lançamento de {entry.Competence} antes de salvar o relatório.");
            }

            // Confere também uma célula calculada do relatório, não apenas a área de entrada.
            if (!string.IsNullOrWhiteSpace(process.FullName) && SheetExists(workbook, "Solicitação"))
            {
                request = workbook.Worksheets["Solicitação"];
                var calculatedName = Convert.ToString(request.Range["D33"].Value2, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(calculatedName))
                    throw new IOException("Os dados foram gravados, mas o Excel não concluiu o cálculo do relatório. Gere novamente com outras planilhas do Excel fechadas.");
            }

            if (TryDate(process.PeriodStart, out var periodStart) && TryDate(process.PeriodEnd, out var periodEnd))
            {
                if (periodEnd < periodStart) (periodStart, periodEnd) = (periodEnd, periodStart);
                for (var year = Math.Max(2006, periodStart.Year); year <= Math.Min(templateMaxYear, periodEnd.Year); year++)
                {
                    var yy = (year % 100).ToString("00", CultureInfo.InvariantCulture);
                    var annualName = $"{yy}-Devido";
                    if (!SheetExists(workbook, annualName)) continue;
                    dynamic? annual = null;
                    try
                    {
                        annual = workbook.Worksheets[annualName];
                        var expected = Convert.ToString(info.Cells[8 + (year - 2006), 5].Value2, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
                        var actual = Convert.ToString(annual.Range["C5"].Value2, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
                        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                            throw new IOException($"O relatório anual de {year} não recebeu o período informado.");
                    }
                    finally { Release(annual); }
                }
            }
        }
        finally
        {
            Release(request);
            Release(launch);
            Release(info);
        }

        static void ValidateCell(dynamic sheet, string address, string? expected, string field)
        {
            if (string.IsNullOrWhiteSpace(expected)) return;
            var actual = Convert.ToString(sheet.Range[address].Value2, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(actual))
                throw new IOException($"O Excel não recebeu o campo {field} antes de salvar o relatório.");
        }
    }

    private static void SelectSheets(dynamic workbook, IReadOnlyList<string> names)
    {
        if (names.Count == 1) { workbook.Worksheets[names[0]].Select(); return; }
        try
        {
            workbook.Worksheets[names.Cast<object>().ToArray()].Select();
        }
        catch
        {
            workbook.Worksheets[names[0]].Select();
            for (var i = 1; i < names.Count; i++) workbook.Worksheets[names[i]].Select(false);
        }
    }

    private static string ExpandBulletinGuide(string value, ExercisePreviousProcess p)
        => (value ?? string.Empty).Replace("{{OM_NOME}}", p.OrganizationName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{BI_NUMERO}}", ExercisePreviousRepository.ExtractBulletinNumber(p.BulletinNumber), StringComparison.OrdinalIgnoreCase);

    private static string FormatDateBr(string value) => TryDate(value, out var date) ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : value ?? string.Empty;
    private static bool TryDate(string? value, out DateTime date)
    {
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy" };
        return DateTime.TryParseExact(value?.Trim(), formats, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out date)
               || DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out date);
    }

    private static string ParsePortugueseMonthYear(string text)
    {
        var normalized = RemoveAccents(text).Trim().ToLowerInvariant();
        var parts = normalized.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var year)) return string.Empty;
        var months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["janeiro"] = 1, ["fevereiro"] = 2, ["marco"] = 3, ["abril"] = 4, ["maio"] = 5, ["junho"] = 6,
            ["julho"] = 7, ["agosto"] = 8, ["setembro"] = 9, ["outubro"] = 10, ["novembro"] = 11, ["dezembro"] = 12
        };
        return months.TryGetValue(parts[0], out var month) ? $"{year:0000}-{month:00}" : string.Empty;
    }

    private static string RemoveAccents(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC);
    }
    private static bool TryDouble(object? value, out double result)
    {
        if (value is double d) { result = d; return true; }
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out result)
               || double.TryParse(Convert.ToString(value, CultureInfo.GetCultureInfo("pt-BR")), NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out result);
    }
    private static bool SheetExists(dynamic workbook, string name)
    {
        try { dynamic sheet = workbook.Worksheets[name]; Release(sheet); return true; }
        catch { return false; }
    }

    private static dynamic FindWorksheet(dynamic workbook, string expectedName)
    {
        try { return workbook.Worksheets[expectedName]; }
        catch { }

        var expected = NormalizeSheetName(expectedName);
        dynamic? worksheets = null;
        try
        {
            worksheets = workbook.Worksheets;
            var count = Convert.ToInt32(worksheets.Count, CultureInfo.InvariantCulture);
            for (var index = 1; index <= count; index++)
            {
                dynamic? sheet = null;
                var matched = false;
                try
                {
                    sheet = worksheets[index];
                    var name = Convert.ToString(sheet.Name, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (NormalizeSheetName(name) != expected) continue;
                    matched = true;
                    return sheet;
                }
                finally
                {
                    if (!matched) Release(sheet);
                }
            }
        }
        finally
        {
            Release(worksheets);
        }

        throw new InvalidOperationException($"A planilha selecionada não possui a aba \"{expectedName}\".");
    }

    private static string NormalizeSheetName(string value)
        => new(RemoveAccents(value)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());


    private static void RemoveSheetProtection(string workbookPath)
    {
        using var archive = ZipFile.Open(workbookPath, ZipArchiveMode.Update);
        var protectedEntries = archive.Entries
            .Where(x =>
                (x.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                 && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                || x.FullName.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var entry in protectedEntries)
        {
            string xml;
            var timestamp = entry.LastWriteTime;
            var fullName = entry.FullName;
            using (var stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false))
                xml = reader.ReadToEnd();

            var updated = System.Text.RegularExpressions.Regex.Replace(
                xml,
                @"<(?:[A-Za-z_][\w.-]*:)?(?:sheetProtection|workbookProtection)\b[^>]*/>|<(?:[A-Za-z_][\w.-]*:)?(?:sheetProtection|workbookProtection)\b[^>]*>.*?</(?:[A-Za-z_][\w.-]*:)?(?:sheetProtection|workbookProtection)>",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

            if (string.Equals(xml, updated, StringComparison.Ordinal)) continue;

            entry.Delete();
            var replacement = archive.CreateEntry(fullName, CompressionLevel.Optimal);
            replacement.LastWriteTime = timestamp;
            using var replacementStream = replacement.Open();
            using var writer = new StreamWriter(replacementStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(updated);
        }
    }

    private static T WithExcel<T>(string path, bool readOnly, bool visible, Func<dynamic, dynamic, T> work)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("O preenchimento do XLSM requer Windows com Microsoft Excel instalado.");
        var excelType = Type.GetTypeFromProgID("Excel.Application") ?? throw new InvalidOperationException("Microsoft Excel não está instalado ou não está registrado no Windows.");
        dynamic? app = null; dynamic? workbook = null;
        try
        {
            app = Activator.CreateInstance(excelType)!;
            app.Visible = visible; app.DisplayAlerts = false; app.ScreenUpdating = false; app.EnableEvents = false;
            try { app.AutomationSecurity = 3; } catch { }
            workbook = app.Workbooks.Open(Path.GetFullPath(path), 0, readOnly, Type.Missing, Type.Missing, Type.Missing, true);
            return work(app, workbook);
        }
        finally
        {
            try { if (workbook is not null) workbook.Close(false); } catch { }
            try { if (app is not null) app.Quit(); } catch { }
            Release(workbook); Release(app);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers();
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { ct.ThrowIfCancellationRequested(); tcs.TrySetResult(action()); }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static void Release(object? com)
    {
        if (com is null) return;
        try
        {
            if (Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        }
        catch { }
    }
}
