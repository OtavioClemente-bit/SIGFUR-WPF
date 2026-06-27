using System.Text.RegularExpressions;
using System.Xml.Linq;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class AssistantAttachmentService
{
    private readonly PdfTextService _pdf;

    public AssistantAttachmentService(PdfTextService pdf) => _pdf = pdf;

    public static string Filter => "Documentos compatíveis|*.pdf;*.docx;*.xlsx;*.txt;*.md;*.csv;*.json;*.xml;*.log|PDF|*.pdf|Word|*.docx|Excel|*.xlsx|Textos|*.txt;*.md;*.csv;*.json;*.xml;*.log|Todos os arquivos|*.*";

    public async Task<string> ExtractAsync(string path, int maxCharacters, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Arquivo não encontrado.", path);

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var text = extension switch
        {
            ".pdf" => await _pdf.ExtractAsync(path, cancellationToken),
            ".docx" => await ReadDocxAsync(path, cancellationToken),
            ".xlsx" => await ReadXlsxAsync(path, cancellationToken),
            ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".log" => await ReadTextAsync(path, cancellationToken),
            _ => throw new NotSupportedException($"O formato {extension} ainda não é compatível com o Assistente SIGFUR.")
        };
        text = Normalize(text);
        if (text.Length > maxCharacters)
            text = text[..maxCharacters] + $"\n\n[Conteúdo limitado a {maxCharacters:N0} caracteres pelo SIGFUR.]";
        return text;
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try { return await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken); }
        catch (DecoderFallbackException)
        {
            return await File.ReadAllTextAsync(path, Encoding.GetEncoding(1252), cancellationToken);
        }
    }

    private static Task<string> ReadDocxAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("Documento Word inválido.");
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var lines = document.Descendants(w + "p")
                .Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value)))
                .Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join(Environment.NewLine, lines);
        }, cancellationToken);

    private static Task<string> ReadXlsxAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(path);
            var shared = new List<string>();
            var sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
            if (sharedEntry is not null)
            {
                using var sharedStream = sharedEntry.Open();
                var sharedDoc = XDocument.Load(sharedStream);
                XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                shared.AddRange(sharedDoc.Descendants(s + "si")
                    .Select(si => string.Concat(si.Descendants(s + "t").Select(t => t.Value))));
            }

            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var builder = new StringBuilder();
            foreach (var entry in archive.Entries
                         .Where(x => x.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                                     && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.AppendLine($"--- {Path.GetFileNameWithoutExtension(entry.Name)} ---");
                using var stream = entry.Open();
                var sheet = XDocument.Load(stream);
                foreach (var row in sheet.Descendants(ns + "row"))
                {
                    var values = new List<string>();
                    foreach (var cell in row.Elements(ns + "c"))
                    {
                        var type = cell.Attribute("t")?.Value;
                        var raw = cell.Element(ns + "v")?.Value ?? cell.Element(ns + "is")?.Element(ns + "t")?.Value ?? string.Empty;
                        if (type == "s" && int.TryParse(raw, out var index) && index >= 0 && index < shared.Count)
                            raw = shared[index];
                        values.Add(raw);
                    }
                    if (values.Any(x => !string.IsNullOrWhiteSpace(x))) builder.AppendLine(string.Join(" | ", values));
                }
            }
            return builder.ToString();
        }, cancellationToken);

    public static string RedactSensitiveData(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var value = text;
        value = Regex.Replace(value, @"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b", "[CPF OCULTO]", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)\b(prec[- ]?cp|prec)\s*[:\-]?\s*[A-Z0-9.\-/]{5,}\b", "$1: [OCULTO]", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?i)\b(conta|ag[eê]ncia|ag\.)\s*[:\-]?\s*[0-9Xx.\-]{3,}\b", "$1: [OCULTO]", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", "[E-MAIL OCULTO]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?<!\d)(?:\+?55\s*)?(?:\(?\d{2}\)?\s*)?9?\d{4}[-\s]?\d{4}(?!\d)", "[TELEFONE OCULTO]", RegexOptions.CultureInvariant);
        return value;
    }

    private static string Normalize(string text)
        => (text ?? string.Empty)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC)
            .Trim();
}
