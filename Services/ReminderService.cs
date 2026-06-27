using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class ReminderService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ReminderService(AppPaths paths, JsonFileService json) { _paths = paths; _json = json; }
    public event EventHandler? Changed;

    public async Task<List<ReminderRecord>> LoadAsync(bool normalizeRecurring = true, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadListAsync(_paths.RemindersFile, cancellationToken);
            if (normalizeRecurring && NormalizeRecurring(records)) await WriteListAsync(_paths.RemindersFile, records, cancellationToken);
            return records.OrderBy(x => x.DueDate ?? DateTime.MaxValue).ThenBy(x => PriorityOrder(x.EffectivePriority)).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<List<ReminderRecord>> LoadHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadListAsync(_paths.ReminderArchiveFile, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ReminderRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadListAsync(_paths.RemindersFile, cancellationToken);
            if (record.Id <= 0) record.Id = records.Count == 0 ? 1 : records.Max(x => x.Id) + 1;
            if (string.IsNullOrWhiteSpace(record.CreatedAt)) record.CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            record.Date = record.DueDate?.ToString("dd/MM/yyyy") ?? string.Empty;
            var index = records.FindIndex(x => x.Id == record.Id);
            if (index >= 0) records[index] = record.Clone(); else records.Add(record.Clone());
            await WriteListAsync(_paths.RemindersFile, records, cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> ApplyAutomaticPrioritiesAsync(CancellationToken cancellationToken = default)
    {
        var changed = false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadListAsync(_paths.RemindersFile, cancellationToken);
            foreach (var item in records)
            {
                var automatic = ReminderRules.AutoPriority(item.DueDate, item.Priority);
                if (automatic.Equals(item.Priority, StringComparison.OrdinalIgnoreCase)) continue;
                item.Priority = automatic;
                changed = true;
            }
            if (changed) await WriteListAsync(_paths.RemindersFile, records, cancellationToken);
        }
        finally { _gate.Release(); }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadListAsync(_paths.RemindersFile, cancellationToken);
            records.RemoveAll(x => x.Id == id);
            await WriteListAsync(_paths.RemindersFile, records, cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetCompletedAsync(int id, bool completed, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadListAsync(_paths.RemindersFile, cancellationToken);
            var item = records.FirstOrDefault(x => x.Id == id);
            if (item is null) return;
            item.Completed = completed;
            if (completed && item.AutoReschedule && !item.Recurrence.Equals("Nenhuma", StringComparison.OrdinalIgnoreCase) && item.DueDate is DateTime due)
            {
                item.Date = ReminderRules.NextDate(due, item.Recurrence).ToString("dd/MM/yyyy");
                item.Completed = false;
            }
            await WriteListAsync(_paths.RemindersFile, records, cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<int> ArchiveCompletedAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadListAsync(_paths.RemindersFile, cancellationToken);
            var history = await ReadListAsync(_paths.ReminderArchiveFile, cancellationToken);
            var completed = records.Where(x => x.Completed).ToList();
            count = completed.Count;
            foreach (var item in completed)
            {
                var copy = item.Clone();
                copy.ArchivedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                history.Add(copy);
                records.Remove(item);
            }
            await WriteListAsync(_paths.RemindersFile, records, cancellationToken);
            await WriteListAsync(_paths.ReminderArchiveFile, history, cancellationToken);
        }
        finally { _gate.Release(); }
        if (count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return count;
    }

    public async Task RestoreFromHistoryAsync(int id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await ReadListAsync(_paths.RemindersFile, cancellationToken);
            var history = await ReadListAsync(_paths.ReminderArchiveFile, cancellationToken);
            var item = history.FirstOrDefault(x => x.Id == id);
            if (item is null) return;
            var restored = item.Clone();
            restored.Id = records.Count == 0 ? 1 : records.Max(x => x.Id) + 1;
            restored.ArchivedAt = string.Empty;
            history.Remove(item);
            records.Add(restored);
            await WriteListAsync(_paths.RemindersFile, records, cancellationToken);
            await WriteListAsync(_paths.ReminderArchiveFile, history, cancellationToken);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteHistoryAsync(int id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var history = await ReadListAsync(_paths.ReminderArchiveFile, cancellationToken);
            history.RemoveAll(x => x.Id == id);
            await WriteListAsync(_paths.ReminderArchiveFile, history, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<ReminderSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_paths.ReminderSettingsFile))
            return await _json.LoadAsync<ReminderSettings>(_paths.ReminderSettingsFile) ?? new ReminderSettings();

        var settings = new ReminderSettings();
        try
        {
            if (await _json.LoadNodeAsync(_paths.LegacyReminderSettingsFile) is JsonObject legacy)
            {
                settings.Search = LegacyString(legacy, "busca", settings.Search);
                settings.Order = LegacyString(legacy, "ordem", settings.Order);
                settings.UpcomingDays = LegacyInt(legacy, "proximos_dias", settings.UpcomingDays);
                settings.ShowCompleted = LegacyBool(legacy, "mostrar_concluidos", settings.ShowCompleted);
                settings.GroupByPriority = LegacyBool(legacy, "agrupar_por_prioridade", settings.GroupByPriority);
                settings.AutoClassify = LegacyBool(legacy, "auto_classificar", settings.AutoClassify);
                await SaveSettingsAsync(settings, cancellationToken);
            }
        }
        catch { }
        return settings;
    }

    public Task SaveSettingsAsync(ReminderSettings settings, CancellationToken cancellationToken = default)
        => _json.SaveAsync(_paths.ReminderSettingsFile, settings);

    public async Task<List<ReminderRecord>> GetUrgentAsync(int upcomingDays = 2, CancellationToken cancellationToken = default)
        => (await LoadAsync(true, cancellationToken)).Where(x => !x.Completed && x.DaysRemaining is int days && days <= Math.Max(0, upcomingDays)).OrderBy(x => x.DueDate).ToList();

    public static ReminderSummary Summarize(IEnumerable<ReminderRecord> records, int upcomingDays)
    {
        var list = records.ToList();
        return new ReminderSummary
        {
            Total = list.Count(x => !x.Completed),
            Overdue = list.Count(x => !x.Completed && x.DaysRemaining < 0),
            Today = list.Count(x => !x.Completed && x.DaysRemaining == 0),
            Upcoming = list.Count(x => !x.Completed && x.DaysRemaining is > 0 && x.DaysRemaining <= Math.Max(0, upcomingDays)),
            NoDate = list.Count(x => !x.Completed && x.DueDate is null),
            Completed = list.Count(x => x.Completed)
        };
    }

    public async Task ExportTextAsync(string path, IEnumerable<ReminderRecord> records, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        foreach (var item in records.OrderBy(x => x.DueDate ?? DateTime.MaxValue))
        {
            builder.AppendLine($"[{item.Id}] {item.Title}");
            builder.AppendLine($"Data: {item.FormattedDate}  |  Prazo: {item.DaysText}  |  Status: {item.Status}");
            builder.AppendLine($"Prioridade: {item.EffectivePriority}  |  Recorrência: {item.Recurrence}");
            if (!string.IsNullOrWhiteSpace(item.Body)) builder.AppendLine(item.Body.Trim());
            builder.AppendLine(new string('-', 72));
        }
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    private static bool NormalizeRecurring(List<ReminderRecord> records)
    {
        var changed = false;
        foreach (var item in records)
        {
            if (item.Completed || item.Recurrence.Equals("Nenhuma", StringComparison.OrdinalIgnoreCase) || item.DueDate is not DateTime date) continue;
            var tries = 0;
            while (date.Date < DateTime.Today && tries++ < 120) date = ReminderRules.NextDate(date, item.Recurrence);
            var formatted = date.ToString("dd/MM/yyyy");
            if (item.Date == formatted) continue;
            item.Date = formatted;
            changed = true;
        }
        return changed;
    }

    private static int PriorityOrder(string? priority) => priority switch { "Urgentíssimo" => 0, "Urgente" => 1, "Normal" => 2, "Baixa" => 3, _ => 9 };

    private static string LegacyString(JsonObject source, string key, string fallback)
    {
        try { return source[key]?.GetValue<string>() ?? fallback; } catch { return fallback; }
    }

    private static int LegacyInt(JsonObject source, string key, int fallback)
    {
        try { return source[key]?.GetValue<int>() ?? fallback; }
        catch
        {
            try { return int.TryParse(source[key]?.ToString(), out var value) ? value : fallback; } catch { return fallback; }
        }
    }

    private static bool LegacyBool(JsonObject source, string key, bool fallback)
    {
        try { return source[key]?.GetValue<bool>() ?? fallback; }
        catch
        {
            try { return bool.TryParse(source[key]?.ToString(), out var value) ? value : fallback; } catch { return fallback; }
        }
    }

    private static async Task<List<ReminderRecord>> ReadListAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<ReminderRecord>>(stream, Options, cancellationToken) ?? [];
        }
        catch { return []; }
    }

    private static async Task WriteListAsync(string path, List<ReminderRecord> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, records, Options, cancellationToken);
        File.Move(temp, path, true);
    }
}
