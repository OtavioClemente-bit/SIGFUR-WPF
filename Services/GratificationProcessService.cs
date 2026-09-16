using System.Text.Json;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class GratificationProcessService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GratificationProcessService(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(_paths.GratificationProcessesDirectory);
    }

    public string GetProcessDirectory(string processId)
    {
        var safeId = new string((processId ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeId)) throw new InvalidOperationException("Identificador do processo inválido.");
        var directory = Path.Combine(_paths.GratificationProcessesDirectory, safeId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string GetReviewDirectory(string processId)
    {
        var directory = Path.Combine(GetProcessDirectory(processId), "conferencia_sped");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public async Task<List<GratificationProcessRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var store = await LoadStoreAsync(cancellationToken);
        return store.Processes.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public async Task<GratificationProcessRecord?> FindAsync(string processId, CancellationToken cancellationToken = default)
    {
        var store = await LoadStoreAsync(cancellationToken);
        return store.Processes.FirstOrDefault(x => x.Id.Equals(processId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<GratificationProcessRecord> SaveDraftAsync(
        GratificationSettings settings,
        IReadOnlyList<GratificationSpedAttachment> attachments,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreCoreAsync(cancellationToken);
            var id = string.IsNullOrWhiteSpace(settings.CurrentProcessId)
                ? $"GRAT-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"
                : settings.CurrentProcessId;
            var record = store.Processes.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (record is null)
            {
                record = new GratificationProcessRecord { Id = id, CreatedAt = DateTime.Now };
                store.Processes.Add(record);
            }

            settings.CurrentProcessId = id;
            record.Title = BuildTitle(settings);
            record.UpdatedAt = DateTime.Now;
            record.IsDeleted = false;
            record.DeletedAt = null;
            record.Status = "Rascunho";
            record.Settings = Clone(settings);
            record.EffectiveByRank = new Dictionary<string, int>(settings.EffectiveByRank, StringComparer.OrdinalIgnoreCase);

            var attachmentsDirectory = Path.Combine(GetProcessDirectory(id), "anexos");
            Directory.CreateDirectory(attachmentsDirectory);
            var wantedFiles = attachments.Select(x => x.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var oldFile in Directory.GetFiles(attachmentsDirectory))
                if (!wantedFiles.Contains(Path.GetFileName(oldFile))) File.Delete(oldFile);
            foreach (var attachment in attachments.Where(x => File.Exists(x.FullPath)))
                File.Copy(attachment.FullPath, Path.Combine(attachmentsDirectory, attachment.FileName), overwrite: true);

            await SaveStoreCoreAsync(store, cancellationToken);
            await SaveRecordFileAsync(record, cancellationToken);
            return record;
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(string processId, SpedAutomationResult result, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreCoreAsync(cancellationToken);
            var record = store.Processes.FirstOrDefault(x => x.Id.Equals(processId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Processo da Grat Rep não encontrado.");
            record.Status = result.Success ? "Salvo no SPED" : "Falha no SPED";
            record.SpedMessage = result.Message;
            record.SpedUrl = result.Url;
            record.ReviewScreenshots = result.ScreenshotPaths.ToList();
            record.UpdatedAt = DateTime.Now;
            await SaveStoreCoreAsync(store, cancellationToken);
            await SaveRecordFileAsync(record, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string processId, bool deleted, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadStoreCoreAsync(cancellationToken);
            var record = store.Processes.FirstOrDefault(x => x.Id.Equals(processId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Processo não encontrado.");
            record.IsDeleted = deleted;
            record.DeletedAt = deleted ? DateTime.Now : null;
            record.UpdatedAt = DateTime.Now;
            await SaveStoreCoreAsync(store, cancellationToken);
            await SaveRecordFileAsync(record, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public GratificationSettings RestoreWorkingCopy(GratificationProcessRecord record)
    {
        var settings = Clone(record.Settings);
        settings.CurrentProcessId = record.Id;
        if (record.EffectiveByRank is { Count: > 0 })
            settings.EffectiveByRank = new Dictionary<string, int>(record.EffectiveByRank, StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(_paths.GratificationSpedAttachmentsDirectory);
        var sourceDirectory = Path.Combine(GetProcessDirectory(record.Id), "anexos");
        settings.RequestAttachmentFiles = [];
        if (Directory.Exists(sourceDirectory))
        {
            var savedNames = (record.Settings.RequestAttachmentFiles ?? [])
                .Select(value => Path.GetFileName(value))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var sources = savedNames.Count > 0
                ? savedNames.Select(name => Path.Combine(sourceDirectory, name)).Where(File.Exists)
                : Directory.GetFiles(sourceDirectory);
            foreach (var source in sources)
            {
                var name = Path.GetFileName(source);
                File.Copy(source, Path.Combine(_paths.GratificationSpedAttachmentsDirectory, name), overwrite: true);
                settings.RequestAttachmentFiles.Add(name);
            }
        }
        return settings;
    }

    private async Task<GratificationProcessStore> LoadStoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await LoadStoreCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<GratificationProcessStore> LoadStoreCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.GratificationProcessesFile)) return new GratificationProcessStore();
        await using var stream = File.OpenRead(_paths.GratificationProcessesFile);
        return await JsonSerializer.DeserializeAsync<GratificationProcessStore>(stream, JsonOptions, cancellationToken)
            ?? new GratificationProcessStore();
    }

    private async Task SaveStoreCoreAsync(GratificationProcessStore store, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.GratificationProcessesFile)!);
        await using var stream = File.Create(_paths.GratificationProcessesFile);
        await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken);
    }

    private async Task SaveRecordFileAsync(GratificationProcessRecord record, CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetProcessDirectory(record.Id), "processo.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
    }

    private static GratificationSettings Clone(GratificationSettings settings)
        => JsonSerializer.Deserialize<GratificationSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions) ?? new GratificationSettings();

    private static string BuildTitle(GratificationSettings settings)
    {
        var location = string.IsNullOrWhiteSpace(settings.RequestLocation) ? "Sem local" : settings.RequestLocation.Trim();
        return $"Grat Rep — {location} — {settings.RequestStartDate:dd/MM/yyyy}";
    }
}
