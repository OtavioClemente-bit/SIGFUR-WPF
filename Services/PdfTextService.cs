using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Extração de texto de PDF totalmente nativa em C#.
/// Usa PdfPig com remontagem por coordenadas para contracheques/tabelas do CPEx
/// e pdftotext apenas como fallback opcional quando já existir na máquina.
/// </summary>
public sealed class PdfTextService
{
    private readonly LogService _log;
    private readonly string? _cacheDirectory;

    public PdfTextService(LogService log, AppPaths? paths = null)
    {
        _log = log;
        if (paths is not null)
        {
            _cacheDirectory = Path.Combine(paths.CacheDirectory, "PdfText");
            Directory.CreateDirectory(_cacheDirectory);
        }
    }

    public async Task<string> ExtractAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        var pages = await ExtractPagesAsync(pdfPath, cancellationToken);
        return Clean(string.Join("\f", pages));
    }

    /// <summary>Reads only the first page for document classification, without extracting the remaining pages.</summary>
    public Task<string> ExtractFirstPageAsync(string pdfPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = PdfDocument.Open(pdfPath);
            if (document.NumberOfPages == 0) return string.Empty;
            var page = document.GetPage(1);
            var text = BuildLayoutText(page);
            return Clean(string.IsNullOrWhiteSpace(text) ? page.Text ?? string.Empty : text);
        }, cancellationToken);

    public async Task<IReadOnlyList<string>> ExtractPagesAsync(string pdfPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            throw new FileNotFoundException("Arquivo PDF não encontrado.", pdfPath);

        try
        {
            var cached = await TryLoadCachedPagesAsync(pdfPath, cancellationToken);
            if (cached is not null) return cached;

            var pages = await Task.Run(() => ExtractPagesWithPdfPig(pdfPath, cancellationToken), cancellationToken);
            if (pages.Any(x => !string.IsNullOrWhiteSpace(x)))
            {
                var cleaned = pages.Select(Clean).ToList();
                await SaveCachedPagesAsync(pdfPath, cleaned, cancellationToken);
                return cleaned;
            }
            var text = await TryPdftotextAsync(pdfPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var cleaned = text.Split('\f').Select(Clean).ToList();
                await SaveCachedPagesAsync(pdfPath, cleaned, cancellationToken);
                return cleaned;
            }
            return pages.Select(Clean).ToList();
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao extrair páginas de PDF em C#.", ex);
            throw;
        }
    }

    private static List<string> ExtractPagesWithPdfPig(string path, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layout = BuildLayoutText(page);
            if (string.IsNullOrWhiteSpace(layout)) layout = page.Text ?? string.Empty;
            result.Add(layout);
        }
        return result;
    }

    private static string BuildLayoutText(Page page)
    {
        var words = page.GetWords().ToList();
        if (words.Count == 0) return string.Empty;

        // Agrupa por linha visual. Contracheques do CPEx costumam vir com várias colunas;
        // page.Text perde essa estrutura e faz a auditoria interpretar tudo errado.
        var rows = words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ThenBy(w => w.BoundingBox.Left)
            .Aggregate(new List<List<Word>>(), (list, word) =>
            {
                var center = (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2d;
                var row = list.FirstOrDefault(r =>
                {
                    var sample = r[0];
                    var sampleCenter = (sample.BoundingBox.Top + sample.BoundingBox.Bottom) / 2d;
                    var tolerance = Math.Max(2.2, Math.Min(7.5, sample.BoundingBox.Height * 0.65));
                    return Math.Abs(sampleCenter - center) <= tolerance;
                });
                if (row is null) list.Add([word]); else row.Add(word);
                return list;
            })
            .Select(r => r.OrderBy(w => w.BoundingBox.Left).ToList())
            .OrderByDescending(r => r.Average(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2d))
            .ToList();

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var line = BuildLine(row);
            if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string BuildLine(IReadOnlyList<Word> row)
    {
        var sb = new StringBuilder();
        double? right = null;
        double medianWidth = row.Select(w => w.Text.Length > 0 ? w.BoundingBox.Width / Math.Max(1, w.Text.Length) : 4).Where(x => x > 0).DefaultIfEmpty(4).Average();
        foreach (var word in row)
        {
            if (right is not null)
            {
                var gap = word.BoundingBox.Left - right.Value;
                var spaces = gap <= medianWidth * 0.75 ? 1 : Math.Clamp((int)Math.Round(gap / Math.Max(3.2, medianWidth * 1.8)), 1, 10);
                sb.Append(' ', spaces);
            }
            sb.Append(word.Text);
            right = word.BoundingBox.Right;
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static async Task<string> TryPdftotextAsync(string pdfPath, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetTempPath(), "SIGFUR", "pdf_text");
        Directory.CreateDirectory(work);
        var output = Path.Combine(work, $"{Guid.NewGuid():N}.txt");
        try
        {
            foreach (var command in new[] { "pdftotext.exe", "pdftotext" })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = command,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };
                    foreach (var argument in new[] { "-layout", "-enc", "UTF-8", pdfPath, output })
                        psi.ArgumentList.Add(argument);
                    using var process = Process.Start(psi);
                    if (process is null) continue;
                    await process.WaitForExitAsync(cancellationToken);
                    if (process.ExitCode != 0 || !File.Exists(output)) continue;
                    var text = await File.ReadAllTextAsync(output, Encoding.UTF8, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                catch { }
            }
            return string.Empty;
        }
        finally
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
        }
    }

    private async Task<IReadOnlyList<string>?> TryLoadCachedPagesAsync(string pdfPath, CancellationToken cancellationToken)
    {
        var cacheFile = CachePath(pdfPath);
        if (cacheFile is null || !File.Exists(cacheFile)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(cacheFile, Encoding.UTF8, cancellationToken);
            var pages = JsonSerializer.Deserialize<List<string>>(json);
            return pages is { Count: > 0 } ? pages : null;
        }
        catch { return null; }
    }

    private async Task SaveCachedPagesAsync(string pdfPath, IReadOnlyList<string> pages, CancellationToken cancellationToken)
    {
        var cacheFile = CachePath(pdfPath);
        if (cacheFile is null || pages.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var json = JsonSerializer.Serialize(pages);
            await File.WriteAllTextAsync(cacheFile, json, Encoding.UTF8, cancellationToken);
            PruneCache();
        }
        catch { }
    }

    private string? CachePath(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(_cacheDirectory)) return null;
        try
        {
            var info = new FileInfo(pdfPath);
            var signature = string.Join("|",
                Path.GetFullPath(pdfPath).ToUpperInvariant(),
                info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "0",
                info.Exists ? info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "0");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
            return Path.Combine(_cacheDirectory, hash + ".json");
        }
        catch { return null; }
    }

    private void PruneCache()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_cacheDirectory) || !Directory.Exists(_cacheDirectory)) return;
            var cutoff = DateTime.UtcNow.AddDays(-45);
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.json").Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTimeUtc).Skip(2500))
                TryDelete(file.FullName);
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.json").Select(x => new FileInfo(x)).Where(x => x.LastWriteTimeUtc < cutoff))
                TryDelete(file.FullName);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string Clean(string value)
        => (value ?? string.Empty)
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
}
