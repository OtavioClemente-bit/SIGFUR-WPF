using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace SIGFUR.Wpf.Services;

public sealed class AssistantDocumentService
{
    private readonly AppPaths _paths;

    public AssistantDocumentService(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(_paths.AssistantExportsDirectory);
    }

    public string GetSuggestedPath(string kind = "DIEx")
    {
        var safeKind = string.Concat((kind ?? "Documento").Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeKind)) safeKind = "Documento";
        return Path.Combine(_paths.AssistantExportsDirectory, $"{safeKind}_{DateTime.Now:yyyyMMdd_HHmmss}.docx");
    }

    public Task<string> ExportDocxAsync(string path, string title, string content, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            var section = new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1701, Header = 567, Footer = 567, Gutter = 0 });

            body.Append(CreateParagraph(title.ToUpperInvariant(), true, 28, JustificationValues.Center, 180));
            body.Append(CreateParagraph($"Gerado pelo Assistente SIGFUR em {DateTime.Now:dd/MM/yyyy HH:mm}", false, 18, JustificationValues.Center, 260));

            foreach (var rawLine in (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = rawLine.TrimEnd();
                if (string.IsNullOrWhiteSpace(line))
                {
                    body.Append(CreateParagraph(string.Empty, false, 24, JustificationValues.Both, 100));
                    continue;
                }
                var isHeading = line.Length <= 90 && (line.EndsWith(':') || line.All(ch => !char.IsLetter(ch) || char.IsUpper(ch)));
                body.Append(CreateParagraph(line, isHeading, 24, JustificationValues.Both, isHeading ? 140 : 80));
            }

            body.Append(CreateParagraph("MINUTA — conferir dados, referências e competência antes da utilização oficial.", true, 17, JustificationValues.Center, 240));
            body.Append(section);
            mainPart.Document.Save();
            return path;
        }, cancellationToken);

    private static Paragraph CreateParagraph(string text, bool bold, int halfPoints, JustificationValues justification, int after)
    {
        var runProperties = new RunProperties(
            new RunFonts { Ascii = "Arial", HighAnsi = "Arial", EastAsia = "Arial", ComplexScript = "Arial" },
            new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) },
            new FontSizeComplexScript { Val = halfPoints.ToString(CultureInfo.InvariantCulture) });
        if (bold) runProperties.Append(new Bold());
        var run = new Run(runProperties, new Text(text ?? string.Empty) { Space = SpaceProcessingModeValues.Preserve });
        var paragraphProperties = new ParagraphProperties(
            new Justification { Val = justification },
            new SpacingBetweenLines { After = after.ToString(CultureInfo.InvariantCulture), Line = "276", LineRule = LineSpacingRuleValues.Auto });
        return new Paragraph(paragraphProperties, run);
    }
}
