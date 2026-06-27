using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class AssistantStorageService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AssistantStorageService(AppPaths paths, JsonFileService json)
    {
        _paths = paths;
        _json = json;
        Directory.CreateDirectory(_paths.AssistantExportsDirectory);
    }

    public async Task<AssistantSettings> LoadSettingsAsync()
    {
        var settings = await _json.LoadAsync<AssistantSettings>(_paths.AssistantSettingsFile) ?? new AssistantSettings();
        settings.Model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-5.4-mini" : settings.Model.Trim();
        settings.ApiBaseUrl = "https://api.openai.com/v1";
        settings.MaxOutputTokens = Math.Clamp(settings.MaxOutputTokens, 256, 16_000);
        settings.MaxHistoryMessages = Math.Clamp(settings.MaxHistoryMessages, 2, 40);
        settings.MaxAttachmentCharacters = Math.Clamp(settings.MaxAttachmentCharacters, 2_000, 150_000);
        settings.MonthlyBudgetBrl = Math.Clamp(settings.MonthlyBudgetBrl, 0, 10_000);
        settings.DollarRate = Math.Clamp(settings.DollarRate, 1, 20);
        return settings;
    }

    public Task SaveSettingsAsync(AssistantSettings settings)
        => _json.SaveAsync(_paths.AssistantSettingsFile, settings);

    public async Task<AssistantConversationStore> LoadHistoryAsync()
        => await _json.LoadAsync<AssistantConversationStore>(_paths.AssistantHistoryFile) ?? new AssistantConversationStore();

    public Task SaveHistoryAsync(AssistantConversationStore store)
    {
        store.UpdatedAt = DateTime.Now;
        return _json.SaveAsync(_paths.AssistantHistoryFile, store);
    }

    public Task ClearHistoryAsync()
        => SaveHistoryAsync(new AssistantConversationStore());

    public async Task<AssistantUsageSummary> GetCurrentMonthUsageAsync(AssistantSettings settings)
    {
        var store = await _json.LoadAsync<AssistantUsageStore>(_paths.AssistantUsageFile) ?? new AssistantUsageStore();
        var firstDay = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var rows = store.Records.Where(x => x.Timestamp >= firstDay).ToList();
        return new AssistantUsageSummary
        {
            Requests = rows.Count,
            InputTokens = rows.Sum(x => x.InputTokens),
            OutputTokens = rows.Sum(x => x.OutputTokens),
            EstimatedCostBrl = rows.Sum(x => x.EstimatedCostBrl),
            BudgetBrl = settings.MonthlyBudgetBrl
        };
    }

    public async Task RecordUsageAsync(AssistantUsageRecord record)
    {
        await _gate.WaitAsync();
        try
        {
            var store = await _json.LoadAsync<AssistantUsageStore>(_paths.AssistantUsageFile) ?? new AssistantUsageStore();
            store.Records.RemoveAll(x => x.Timestamp < DateTime.Today.AddYears(-1));
            store.Records.Add(record);
            await _json.SaveAsync(_paths.AssistantUsageFile, store);
        }
        finally { _gate.Release(); }
    }

    public static (decimal InputUsdPerMillion, decimal OutputUsdPerMillion) GetModelRates(string model)
    {
        var normalized = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.StartsWith("gpt-5.5", StringComparison.Ordinal)) return (5m, 30m);
        if (normalized.StartsWith("gpt-5.4-mini", StringComparison.Ordinal)) return (0.75m, 4.50m);
        if (normalized.StartsWith("gpt-5.4-nano", StringComparison.Ordinal)) return (0.20m, 1.25m);
        if (normalized.StartsWith("gpt-5.4", StringComparison.Ordinal)) return (2.50m, 15m);
        if (normalized.StartsWith("gpt-5-mini", StringComparison.Ordinal)) return (0.25m, 2m);
        if (normalized.StartsWith("gpt-5-nano", StringComparison.Ordinal)) return (0.05m, 0.40m);
        return (0.75m, 4.50m);
    }

    public static AssistantUsageRecord CalculateUsage(string model, int inputTokens, int outputTokens, decimal dollarRate)
    {
        var rates = GetModelRates(model);
        var usd = inputTokens / 1_000_000m * rates.InputUsdPerMillion
                  + outputTokens / 1_000_000m * rates.OutputUsdPerMillion;
        return new AssistantUsageRecord
        {
            Timestamp = DateTime.Now,
            Model = model,
            InputTokens = Math.Max(0, inputTokens),
            OutputTokens = Math.Max(0, outputTokens),
            EstimatedCostUsd = usd,
            EstimatedCostBrl = usd * dollarRate
        };
    }
}
