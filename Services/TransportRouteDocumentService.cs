using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SIGFUR.Wpf.Models;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace SIGFUR.Wpf.Services;

public sealed class TransportRouteDocumentService
{
    private const int PageWidthDxa = 11906;
    private const int PageHeightDxa = 16838;
    private const int MarginDxa = 1021;
    private const int ContentWidthDxa = PageWidthDxa - (MarginDxa * 2);
    private const string Navy = "17365D";
    private const string Blue = "1F4E78";
    private const string LightBlue = "DCE6F1";
    private const string LightGray = "F2F4F7";
    private const string Border = "B7C5D5";
    private const string Muted = "5B6573";

    private readonly AppPaths _paths;
    private readonly LogService _log;

    public TransportRouteDocumentService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<TransportRouteDocumentResult> GenerateAsync(
        TransportRouteDocumentData data,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(data.Military);
        cancellationToken.ThrowIfCancellationRequested();

        var output = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(_paths.GeneratedDocumentsDirectory, "auxilio_transporte", "fichas_rota", DateTime.Now.ToString("yyyyMMdd_HHmmss"))
            : outputDirectory;
        Directory.CreateDirectory(output);

        var displayName = string.IsNullOrWhiteSpace(data.Military.WarName) ? data.Military.Name : data.Military.WarName;
        var stem = SafeFileName($"ficha_rota_{data.Military.ShortRank}_{displayName}_{DateTime.Now:yyyyMMdd_HHmmss}");
        var docxPath = UniquePath(Path.Combine(output, stem + ".docx"));
        var attachmentPages = await PrepareResidencePagesAsync(data.ResidenceProofPath, Path.Combine(output, "anexos_renderizados"), cancellationToken);
        await Task.Run(() => CreateDocx(docxPath, data, cancellationToken, attachmentPages), cancellationToken);

        string? pdfPath = null;
        string? warning = null;
        try
        {
            pdfPath = await ConvertToPdfAsync(docxPath, cancellationToken);
        }
        catch (Exception ex)
        {
            warning = "O DOCX foi criado, mas não foi possível gerar o PDF automaticamente. O documento pode ser aberto e impresso pelo Word ou LibreOffice.";
            await _log.WriteAsync("Falha ao converter a ficha profissional da rota para PDF.", ex);
        }

        return new TransportRouteDocumentResult(docxPath, pdfPath, warning);
    }

    private static void CreateDocx(string path, TransportRouteDocumentData data, CancellationToken cancellationToken, IReadOnlyList<string>? attachmentPages = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        AddStyles(mainPart);
        var body = mainPart.Document.Body!;

        AddHeaderAndFooter(mainPart);
        body.Append(Paragraph("EXÉRCITO BRASILEIRO", "Kicker", JustificationValues.Center));
        body.Append(Paragraph("4ª COMPANHIA DE POLÍCIA DO EXÉRCITO", "Kicker", JustificationValues.Center));
        body.Append(Paragraph("FICHA DE ROTA E MEMÓRIA DE CÁLCULO", "Title", JustificationValues.Center));
        body.Append(Paragraph("AUXÍLIO-TRANSPORTE", "Subtitle", JustificationValues.Center));
        body.Append(Paragraph($"Documento individual • Emitido em {DateTime.Now:dd/MM/yyyy 'às' HH:mm}", "Metadata", JustificationValues.Center));

        body.Append(Heading("1. Identificação do beneficiário"));
        body.Append(CreateLabelTable(
            [
                ("Militar", $"{data.Military.ShortRank} {data.Military.Name}"),
                ("Nome de guerra", data.Military.WarName),
                ("CPF", data.Military.FormattedCpf),
                ("PREC-CP / Identidade", JoinValues(data.Military.PrecCp, data.Military.MilitaryId)),
                ("Período de referência", string.IsNullOrWhiteSpace(data.Reference) ? DateTime.Today.ToString("MM/yyyy") : data.Reference),
                ("Dias úteis", Math.Max(0, data.WorkingDays).ToString(CultureInfo.GetCultureInfo("pt-BR")))
            ]));

        body.Append(Heading("2. Rota declarada"));
        body.Append(CreateLabelTable(
            [
                ("Origem", Blank(data.Origin)),
                ("Destino", Blank(data.Destination))
            ]));

        body.Append(Heading("3. Linhas e tarifas utilizadas"));
        body.Append(CreateBusTable(data.Buses));

        body.Append(Heading("4. Memória de cálculo"));
        body.Append(CreateCalculationTable(data));
        body.Append(Paragraph(
            "Critério: soma das tarifas de uma passagem × 2 (ida e volta) × dias úteis, deduzida a cota-parte regulamentar calculada pelo SIGFUR.",
            "Note",
            JustificationValues.Left));

        body.Append(Heading("5. Comprovante visual da rota"));
        if (!string.IsNullOrWhiteSpace(data.ScreenshotPath) && File.Exists(data.ScreenshotPath))
        {
            body.Append(CreateImageParagraph(mainPart, data.ScreenshotPath));
            body.Append(Paragraph(
                $"Figura 1 — Rota entre {Blank(data.Origin)} e {Blank(data.Destination)}.",
                "Caption",
                JustificationValues.Center));
        }
        else
        {
            body.Append(Paragraph("Nenhum print de rota foi anexado a esta ficha.", "Warning", JustificationValues.Center));
        }

        body.Append(Paragraph(
            "Documento destinado à conferência administrativa e à instrução do processo de Auxílio-Transporte.",
            "FooterNote",
            JustificationValues.Center));

        if (attachmentPages is { Count: > 0 })
        {
            for (var index = 0; index < attachmentPages.Count; index++)
            {
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                body.Append(Heading($"ANEXO · COMPROVANTE DE RESIDÊNCIA ({index + 1}/{attachmentPages.Count})"));
                body.Append(Paragraph($"{data.Military.ShortRank} {data.Military.Name}", "Metadata", JustificationValues.Center));
                body.Append(CreateImageParagraph(mainPart, attachmentPages[index], 7_800_000L));
            }
        }

        body.Append(new SectionProperties(
            new HeaderReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(mainPart.HeaderParts.Single()) },
            new FooterReference { Type = HeaderFooterValues.Default, Id = mainPart.GetIdOfPart(mainPart.FooterParts.Single()) },
            new PageSize { Width = PageWidthDxa, Height = PageHeightDxa },
            new PageMargin
            {
                Top = MarginDxa,
                Right = (UInt32Value)(uint)MarginDxa,
                Bottom = MarginDxa,
                Left = (UInt32Value)(uint)MarginDxa,
                Header = 500,
                Footer = 500,
                Gutter = 0
            }));
        mainPart.Document.Save();
    }

    private static async Task<IReadOnlyList<string>> PrepareResidencePagesAsync(string path, string output, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        if (!File.Exists(path)) throw new FileNotFoundException("Comprovante de residência não encontrado.", path);
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ReadImageSize(path); // Reject unreadable images before producing a partial document.
            return [path];
        }
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        Directory.CreateDirectory(output);
        var pages = new List<string>();
        for (uint index = 0; index < pdf.PageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = pdf.GetPage(index);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1600 });
            stream.Seek(0);
            using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[(int)stream.Size]; reader.ReadBytes(bytes);
            var imagePath = Path.Combine(output, $"residencia_{Guid.NewGuid():N}_{index + 1}.png");
            await File.WriteAllBytesAsync(imagePath, bytes, cancellationToken); pages.Add(imagePath);
        }
        return pages;
    }

    private static void AddStyles(MainDocumentPart mainPart)
    {
        var part = mainPart.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            Style("Normal", "Normal", 21, "222222", false, 0, 90),
            Style("Kicker", "Kicker", 19, Navy, true, 0, 15),
            Style("Title", "Title", 32, Navy, true, 80, 20),
            Style("Subtitle", "Subtitle", 24, Blue, true, 0, 30),
            Style("Metadata", "Metadata", 18, Muted, false, 0, 130),
            Style("Heading1", "Heading 1", 23, Blue, true, 150, 55, keepNext: true),
            Style("TableText", "Table Text", 18, "222222", false, 0, 0),
            Style("TableHeader", "Table Header", 18, "FFFFFF", true, 0, 0),
            Style("Note", "Note", 17, Muted, false, 55, 55),
            Style("Warning", "Warning", 19, "8A4B08", true, 100, 100),
            Style("Caption", "Caption", 17, Muted, false, 35, 75),
            Style("FooterNote", "Footer Note", 16, Muted, false, 100, 0));
        part.Styles.Save();
    }

    private static Style Style(
        string id,
        string name,
        int halfPoints,
        string color,
        bool bold,
        int before,
        int after,
        bool keepNext = false)
    {
        var paragraphProperties = new StyleParagraphProperties(
            new SpacingBetweenLines
            {
                Before = before.ToString(CultureInfo.InvariantCulture),
                After = after.ToString(CultureInfo.InvariantCulture),
                Line = "264",
                LineRule = LineSpacingRuleValues.Auto
            });
        if (keepNext) paragraphProperties.Append(new KeepNext());
        var runProperties = new StyleRunProperties(
            new RunFonts { Ascii = "Arial", HighAnsi = "Arial", EastAsia = "Arial", ComplexScript = "Arial" },
            new Color { Val = color },
            new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) },
            new FontSizeComplexScript { Val = halfPoints.ToString(CultureInfo.InvariantCulture) });
        if (bold) runProperties.Append(new Bold());
        return new Style(new StyleName { Val = name }, paragraphProperties, runProperties)
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            Default = id == "Normal"
        };
    }

    private static void AddHeaderAndFooter(MainDocumentPart mainPart)
    {
        var headerPart = mainPart.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(Paragraph("SIGFUR  •  AUXÍLIO-TRANSPORTE", "FooterNote", JustificationValues.Right));
        headerPart.Header.Save();

        var footerPart = mainPart.AddNewPart<FooterPart>();
        var footerParagraph = Paragraph(string.Empty, "FooterNote", JustificationValues.Center);
        footerParagraph.Append(new Run(new Text($"{OrganizationIdentity.Name}  •  Página\u00A0")));
        footerParagraph.Append(new SimpleField { Instruction = "PAGE" });
        footerPart.Footer = new Footer(footerParagraph);
        footerPart.Footer.Save();
    }

    private static Paragraph Heading(string text) => Paragraph(text, "Heading1", JustificationValues.Left);

    private static Paragraph Paragraph(string text, string styleId, JustificationValues alignment)
        => new(
            new ParagraphProperties(
                new ParagraphStyleId { Val = styleId },
                new Justification { Val = alignment }),
            new Run(new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve }));

    private static Table CreateLabelTable(IReadOnlyList<(string Label, string Value)> rows)
    {
        var table = CreateTable([2200, ContentWidthDxa - 2200], LightGray);
        foreach (var (label, value) in rows)
        {
            table.Append(new TableRow(
                Cell(label, 2200, "TableText", LightGray, bold: true),
                Cell(Blank(value), ContentWidthDxa - 2200, "TableText", "FFFFFF")));
        }
        return table;
    }

    private static Table CreateBusTable(IReadOnlyList<TransportRouteDocumentBus> buses)
    {
        var widths = new[] { 550, 1300, 2940, 1450, 1720, 1904 };
        var table = CreateTable(widths, Blue);
        table.Append(new TableRow(
            new TableRowProperties(new TableHeader()),
            Cell("#", widths[0], "TableHeader", Blue, alignment: JustificationValues.Center),
            Cell("Linha", widths[1], "TableHeader", Blue, alignment: JustificationValues.Center),
            Cell("Nome / trajeto", widths[2], "TableHeader", Blue),
            Cell("Categoria", widths[3], "TableHeader", Blue),
            Cell("1 passagem", widths[4], "TableHeader", Blue, alignment: JustificationValues.Right),
            Cell("Ida + volta", widths[5], "TableHeader", Blue, alignment: JustificationValues.Right)));

        var rows = buses?.ToList() ?? [];
        if (rows.Count == 0)
        {
            table.Append(new TableRow(
                Cell("—", widths[0], "TableText", "FFFFFF", alignment: JustificationValues.Center),
                Cell("Nenhuma linha cadastrada", widths.Skip(1).Sum(), "TableText", "FFFFFF", gridSpan: 5)));
        }
        else
        {
            for (var index = 0; index < rows.Count; index++)
            {
                var item = rows[index];
                var fill = index % 2 == 0 ? "FFFFFF" : "F8FAFC";
                table.Append(new TableRow(
                    Cell((index + 1).ToString(CultureInfo.InvariantCulture), widths[0], "TableText", fill, alignment: JustificationValues.Center),
                    Cell(Blank(item.Number), widths[1], "TableText", fill, alignment: JustificationValues.Center),
                    Cell(Blank(item.Name), widths[2], "TableText", fill),
                    Cell(Blank(item.Category), widths[3], "TableText", fill),
                    Cell(Money(item.Fare), widths[4], "TableText", fill, alignment: JustificationValues.Right),
                    Cell(Money(item.Fare * 2m), widths[5], "TableText", fill, alignment: JustificationValues.Right)));
            }
        }

        table.Append(new TableRow(
            Cell("TOTAL DIÁRIO", widths.Take(4).Sum(), "TableText", LightBlue, bold: true, alignment: JustificationValues.Right, gridSpan: 4),
            Cell(Money(rows.Sum(x => Math.Max(0m, x.Fare))), widths[4], "TableText", LightBlue, bold: true, alignment: JustificationValues.Right),
            Cell(Money(rows.Sum(x => Math.Max(0m, x.Fare)) * 2m), widths[5], "TableText", LightBlue, bold: true, alignment: JustificationValues.Right)));
        return table;
    }

    private static Table CreateCalculationTable(TransportRouteDocumentData data)
    {
        var table = CreateTable([ContentWidthDxa - 2200, 2200], LightGray);
        var rows = new[]
        {
            ("Valor diário (ida e volta)", data.DailyGross),
            ($"Valor mensal bruto ({Math.Max(0, data.WorkingDays)} dias úteis)", data.MonthGross),
            ("Cota-parte do militar", data.Share),
            ("Valor mensal líquido", data.Net)
        };
        foreach (var (label, value) in rows)
        {
            var highlight = label.StartsWith("Valor mensal líquido", StringComparison.Ordinal) ? LightBlue : "FFFFFF";
            table.Append(new TableRow(
                Cell(label, ContentWidthDxa - 2200, "TableText", highlight, bold: label.StartsWith("Valor mensal líquido", StringComparison.Ordinal)),
                Cell(Money(value), 2200, "TableText", highlight, bold: label.StartsWith("Valor mensal líquido", StringComparison.Ordinal), alignment: JustificationValues.Right)));
        }
        return table;
    }

    private static Table CreateTable(IReadOnlyList<int> widths, string headerFill)
    {
        var table = new Table();
        table.Append(new TableProperties(
            new TableWidth { Width = ContentWidthDxa.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
            new TableIndentation { Width = 120, Type = TableWidthUnitValues.Dxa },
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableBorders(
                BorderElement<TopBorder>(), BorderElement<LeftBorder>(), BorderElement<BottomBorder>(),
                BorderElement<RightBorder>(), BorderElement<InsideHorizontalBorder>(), BorderElement<InsideVerticalBorder>()),
            new TableCellMarginDefault(
                new TopMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                new TableCellLeftMargin { Width = 120, Type = TableWidthValues.Dxa },
                new BottomMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                new TableCellRightMargin { Width = 120, Type = TableWidthValues.Dxa })));
        table.Append(new TableGrid(widths.Select(width => new GridColumn { Width = width.ToString(CultureInfo.InvariantCulture) })));
        return table;
    }

    private static T BorderElement<T>() where T : BorderType, new()
        => new() { Val = BorderValues.Single, Color = Border, Size = 5 };

    private static TableCell Cell(
        string text,
        int width,
        string style,
        string fill,
        bool bold = false,
        JustificationValues? alignment = null,
        int gridSpan = 1)
    {
        var properties = new TableCellProperties(
            new TableCellWidth { Width = width.ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Dxa },
            new Shading { Val = ShadingPatternValues.Clear, Fill = fill },
            new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
        if (gridSpan > 1) properties.Append(new GridSpan { Val = gridSpan });
        var paragraph = Paragraph(text, style, alignment ?? JustificationValues.Left);
        if (bold) paragraph.Descendants<Run>().First().RunProperties = new RunProperties(new Bold());
        return new TableCell(properties, paragraph);
    }

    private static Paragraph CreateImageParagraph(MainDocumentPart mainPart, string imagePath, long maxHeight = 3_250_000L)
    {
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        var imageType = extension is ".jpg" or ".jpeg" ? ImagePartType.Jpeg
            : extension == ".gif" ? ImagePartType.Gif
            : extension == ".bmp" ? ImagePartType.Bmp
            : ImagePartType.Png;
        var imagePart = mainPart.AddImagePart(imageType);
        using (var stream = File.OpenRead(imagePath)) imagePart.FeedData(stream);

        var (pixelWidth, pixelHeight) = ReadImageSize(imagePath);
        const long maxWidth = 6_250_000L;
        var ratio = Math.Min((double)maxWidth / Math.Max(1, pixelWidth), (double)maxHeight / Math.Max(1, pixelHeight));
        var cx = (long)Math.Round(pixelWidth * ratio);
        var cy = (long)Math.Round(pixelHeight * ratio);
        var relationshipId = mainPart.GetIdOfPart(imagePart);

        var drawing = new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = (uint)mainPart.ImageParts.Count(), Name = Path.GetFileName(imagePath), Description = "Anexo da ficha de auxílio-transporte" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = Path.GetFileName(imagePath) },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId, CompressionState = A.BlipCompressionValues.Print },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            });
        var paragraph = Paragraph(string.Empty, "Normal", JustificationValues.Center);
        paragraph.Append(new Run(drawing));
        return paragraph;
    }

    private static (int Width, int Height) ReadImageSize(string path)
    {
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            new Uri(Path.GetFullPath(path), UriKind.Absolute),
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        return (Math.Max(1, frame.PixelWidth), Math.Max(1, frame.PixelHeight));
    }

    private static async Task<string?> ConvertToPdfAsync(string docxPath, CancellationToken cancellationToken)
    {
        var soffice = FindLibreOffice();
        if (string.IsNullOrWhiteSpace(soffice)) return null;
        var output = Path.GetDirectoryName(docxPath)!;
        var profile = Path.Combine(Path.GetTempPath(), "sigfur_route_pdf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        try
        {
            var profileUri = new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');
            var startInfo = new ProcessStartInfo
            {
                FileName = soffice,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("--nodefault");
            startInfo.ArgumentList.Add("--nolockcheck");
            startInfo.ArgumentList.Add($"-env:UserInstallation={profileUri}");
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add("pdf:writer_pdf_Export");
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(output);
            startInfo.ArgumentList.Add(docxPath);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar o LibreOffice.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Em máquinas com o LibreOffice iniciando pela primeira vez, a conversão pode
            // levar quase um minuto. Aguarda o resultado em vez de informar falha prematura.
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException("O LibreOffice demorou mais de 90 segundos para gerar o PDF.");
            }
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            var pdf = Path.Combine(output, Path.GetFileNameWithoutExtension(docxPath) + ".pdf");
            if (process.ExitCode != 0 || !File.Exists(pdf) || new FileInfo(pdf).Length == 0)
                throw new InvalidOperationException("O LibreOffice não conseguiu gerar o PDF." + (string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error.Trim()));
            return pdf;
        }
        finally
        {
            try { Directory.Delete(profile, recursive: true); } catch { }
        }
    }

    private static string? FindLibreOffice()
        => new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LibreOffice", "program", "soffice.exe")
        }.FirstOrDefault(File.Exists);

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "Não informado" : value.Trim();
    private static string JoinValues(params string?[] values) => string.Join(" / ", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
    private static string Money(decimal value) => Math.Max(0m, value).ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));

    private static string SafeFileName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) value = value.Replace(ch, '_');
        value = Regex.Replace(value, @"\s+", "_").Trim('_', '.');
        return value.Length > 140 ? value[..140] : value;
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired) && !File.Exists(Path.ChangeExtension(desired, ".pdf"))) return desired;
        var directory = Path.GetDirectoryName(desired) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desired);
        var extension = Path.GetExtension(desired);
        for (var index = 2; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}_{index}{extension}");
            if (!File.Exists(candidate) && !File.Exists(Path.ChangeExtension(candidate, ".pdf"))) return candidate;
        }
        return Path.Combine(directory, $"{name}_{Guid.NewGuid():N}{extension}");
    }
}

public sealed class TransportRouteDocumentData
{
    public required MilitaryRecord Military { get; init; }
    public string Origin { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string Reference { get; init; } = string.Empty;
    public string ScreenshotPath { get; init; } = string.Empty;
    public string ResidenceProofPath { get; init; } = string.Empty;
    public int WorkingDays { get; init; }
    public decimal DailyGross { get; init; }
    public decimal MonthGross { get; init; }
    public decimal Share { get; init; }
    public decimal Net { get; init; }
    public IReadOnlyList<TransportRouteDocumentBus> Buses { get; init; } = [];
}

public sealed record TransportRouteDocumentBus(string Number, string Name, string Category, decimal Fare);

public sealed record TransportRouteDocumentResult(string DocxPath, string? PdfPath, string? Warning)
{
    public string PrintablePath => !string.IsNullOrWhiteSpace(PdfPath) ? PdfPath : DocxPath;
}
