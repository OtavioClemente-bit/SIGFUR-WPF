using System.Globalization;
using System.Text;
using System.Threading.Channels;
using System.Collections.Concurrent;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Registra matérias confirmadas pelo SISBOL sem acrescentar espera ao fluxo de envio.
/// Uma única fila serializa as gravações para não perder registros concorrentes.
/// </summary>
public sealed class SisbolSubmissionHistoryService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;
    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SisbolSubmissionHistoryEntry> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<SisbolSubmissionHistoryEntry> _queue = Channel.CreateUnbounded<SisbolSubmissionHistoryEntry>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly Task _worker;
    private bool _disposed;

    public SisbolSubmissionHistoryService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths;
        _json = json;
        _log = log;
        _worker = Task.Run(ProcessQueueAsync);
    }

    public void Enqueue(SisbolMatterPayload payload, IReadOnlyList<MilitaryRecord> military)
    {
        if (_disposed) return;
        var entry = new SisbolSubmissionHistoryEntry
        {
            SentAt = DateTimeOffset.Now,
            Category = InferCategory(payload.SpecificSubject, payload.OpeningTextPlain),
            GeneralSubject = payload.GeneralSubject?.Trim() ?? string.Empty,
            Subject = payload.SpecificSubject?.Trim() ?? string.Empty,
            OpeningText = payload.OpeningTextPlain?.Trim() ?? string.Empty,
            ClosingText = payload.IncludeConsequences ? payload.ClosingTextPlain?.Trim() ?? string.Empty : string.Empty,
            IncludedClosingText = payload.IncludeConsequences,
            Military = (military ?? [])
                .Where(x => x is not null)
                .DistinctBy(x => x.Id)
                .Select(x => new SisbolSubmissionMilitary
                {
                    Id = x.Id,
                    Rank = x.ShortRank?.Trim() ?? string.Empty,
                    Name = x.Name?.Trim() ?? string.Empty,
                    WarName = x.WarName?.Trim() ?? string.Empty
                })
                .ToList()
        };
        _pending[entry.Id] = entry;
        if (!_queue.Writer.TryWrite(entry)) _pending.TryRemove(entry.Id, out _);
    }

    public async Task<IReadOnlyList<SisbolSubmissionHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _storeGate.WaitAsync(cancellationToken);
        try
        {
            var store = await _json.LoadAsync<SisbolSubmissionHistoryStore>(_paths.SisbolSubmissionHistoryFile)
                        ?? new SisbolSubmissionHistoryStore();
            store.Items ??= [];
            return store.Items
                .Concat(_pending.Values)
                .Where(x => x is not null)
                .DistinctBy(x => x.Id)
                .OrderByDescending(x => x.SentAt)
                .ToList();
        }
        finally { _storeGate.Release(); }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    await SaveEntryAsync(entry);
                    _pending.TryRemove(entry.Id, out _);
                }
                catch (Exception ex) { await _log.WriteAsync("Falha ao salvar o histórico de envios ao SISBOL.", ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task SaveEntryAsync(SisbolSubmissionHistoryEntry entry)
    {
        await _storeGate.WaitAsync();
        try
        {
            var store = await _json.LoadAsync<SisbolSubmissionHistoryStore>(_paths.SisbolSubmissionHistoryFile)
                        ?? new SisbolSubmissionHistoryStore();
            store.Items ??= [];
            store.Items.Insert(0, entry);
            await _json.SaveAsync(_paths.SisbolSubmissionHistoryFile, store);
        }
        finally { _storeGate.Release(); }
    }

    private static string InferCategory(string? subject, string? body)
    {
        var normalized = Normalize($"{subject} {body}");
        if (normalized.Contains("GRATIFICACAO DE REPRESENTACAO", StringComparison.Ordinal)) return "Gratificação de Representação";
        if (normalized.Contains("AUXILIO TRANSPORTE", StringComparison.Ordinal) || normalized.Contains("AUX TRANSPORTE", StringComparison.Ordinal)) return "Auxílio-Transporte";
        if (normalized.Contains("PENSAO JUDICIAL", StringComparison.Ordinal)) return "Pensão Judicial";
        if (normalized.Contains("FERIAS", StringComparison.Ordinal)) return "Férias";
        if (normalized.Contains("AJUSTE DE CONTAS", StringComparison.Ordinal)) return "Ajuste de Contas";
        if (normalized.Contains("PRE ESCOLAR", StringComparison.Ordinal)) return "Auxílio Pré-Escolar";
        return "Boletim";
    }

    private static string Normalize(string value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = true;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }
        return builder.ToString().Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        try
        {
            if (!_worker.Wait(TimeSpan.FromSeconds(3)))
            {
                _stop.Cancel();
                try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
            }
        }
        catch { _stop.Cancel(); }
        _stop.Dispose();
        if (_worker.IsCompleted) _storeGate.Dispose();
    }
}
