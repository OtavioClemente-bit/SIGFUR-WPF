namespace SIGFUR.Wpf.Services;

public sealed partial class PaymentConferenceService
{
    private sealed class PaystubPeriodEntry
    {
        public string Path { get; set; } = string.Empty;
        public long Length { get; set; }
        public long ModifiedTicks { get; set; }
        public int Month { get; set; }
        public int Year { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    private async Task<List<PaystubPeriodEntry>> ReadPeriodIndexAsync(IReadOnlyList<string> files,
        IProgress<string>? progress, CancellationToken token)
    {
        var cachePath = Path.Combine(_paths.PaymentConferenceCacheDirectory, "paystub-period-index-v1.json");
        var saved = await _json.LoadAsync<List<PaystubPeriodEntry>>(cachePath) ?? [];
        var byPath = saved.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var entries = new List<PaystubPeriodEntry>();
        var changed = false;
        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var path = files[i];
            var info = new FileInfo(path);
            if (byPath.TryGetValue(path, out var previous) && previous.Length == info.Length
                && previous.ModifiedTicks == info.LastWriteTimeUtc.Ticks && previous.Error.Length == 0)
            {
                entries.Add(previous);
                continue;
            }
            progress?.Report($"Identificando mês/ano no cabeçalho {i + 1}/{files.Count} (sem conferir rubricas): {Path.GetFileName(path)}");
            var entry = new PaystubPeriodEntry { Path = path, Length = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks };
            try
            {
                var firstPage = await _pdfText.ExtractFirstPageAsync(path, token);
                var periods = ReadPayrollPeriods(PayrollHeader(firstPage));
                if (periods.Count == 1) { entry.Month = periods[0].Month; entry.Year = periods[0].Year; }
                else entry.Error = periods.Count == 0 ? "Competência não identificada no cabeçalho." : "Mais de uma competência no cabeçalho.";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { entry.Error = ex.Message; }
            entries.Add(entry); byPath[path] = entry; changed = true;
        }
        if (changed)
        {
            try { await _json.SaveAsync(cachePath, byPath.Values.ToList()); }
            catch (Exception ex) { await _log.WriteAsync("Não foi possível salvar o índice de competências dos contracheques.", ex); }
        }
        return entries;
    }

    private static string PayrollHeader(string text)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"(?im)^.*(?:ACESSADO POR|ACESSO EM|EMITIDO POR).*$", "");
        var firstCode = RubricCodeRegex().Match(cleaned.ToUpperInvariant());
        return firstCode.Success ? cleaned[..firstCode.Index] : cleaned;
    }
}
