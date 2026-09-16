using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class AssistantStorageService
{
    private sealed class ProtectedHistoryFile
    {
        public int Version { get; set; } = 1;
        public string Protection { get; set; } = "Windows-DPAPI-CurrentUser";
        public string ProtectedPayload { get; set; } = string.Empty;
    }

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
        settings.Provider = AssistantCredentialService.IsDeepSeek(settings.Provider) ? "DeepSeek" : "OpenAI";
        settings.Model = string.IsNullOrWhiteSpace(settings.Model)
            ? (settings.Provider == "DeepSeek" ? "deepseek-v4-flash" : "gpt-5.6-luna") : settings.Model.Trim();
        settings.SchemaVersion = 4;
        settings.ApiBaseUrl = settings.Provider == "DeepSeek" ? "https://api.deepseek.com" : "https://api.openai.com/v1";
        settings.MaxOutputTokens = Math.Clamp(settings.MaxOutputTokens, 256, 16_000);
        settings.MaxHistoryMessages = Math.Clamp(settings.MaxHistoryMessages, 2, 40);
        settings.MaxAttachmentCharacters = Math.Clamp(settings.MaxAttachmentCharacters, 2_000, 150_000);
        settings.MonthlyBudgetBrl = Math.Clamp(settings.MonthlyBudgetBrl, 0, 10_000);
        settings.DollarRate = Math.Clamp(settings.DollarRate, 1, 20);
        settings.ReasoningEffort = settings.ReasoningEffort is "none" or "low" or "medium" or "high" or "xhigh" or "max" ? settings.ReasoningEffort : "medium";
        settings.MaxToolRounds = Math.Clamp(settings.MaxToolRounds, 2, 12);
        settings.MaxContextCharacters = Math.Clamp(settings.MaxContextCharacters, 20_000, 120_000);
        settings.DeadlineLookaheadDays = Math.Clamp(settings.DeadlineLookaheadDays, 1, 30);
        settings.OperatorInstructions ??= string.Empty;
        settings.DiexRitex ??= string.Empty;
        return settings;
    }

    public Task SaveSettingsAsync(AssistantSettings settings)
        => _json.SaveAsync(_paths.AssistantSettingsFile, settings);

    public async Task<AssistantConversationStore> LoadHistoryAsync()
    {
        var protectedFile = await _json.LoadAsync<ProtectedHistoryFile>(_paths.AssistantHistoryFile);
        if (!string.IsNullOrWhiteSpace(protectedFile?.ProtectedPayload))
        {
            var plainJson = WindowsSecretProtector.Unprotect(protectedFile.ProtectedPayload);
            if (string.IsNullOrWhiteSpace(plainJson)) return new AssistantConversationStore();
            try
            {
                return JsonSerializer.Deserialize<AssistantConversationStore>(plainJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new AssistantConversationStore();
            }
            catch { return new AssistantConversationStore(); }
        }

        // Migração transparente do formato antigo em texto puro para DPAPI do usuário atual.
        var legacy = await _json.LoadAsync<AssistantConversationStore>(_paths.AssistantHistoryFile);
        if (legacy is null) return new AssistantConversationStore();
        await SaveHistoryAsync(legacy);
        return legacy;
    }

    public Task SaveHistoryAsync(AssistantConversationStore store)
    {
        store.UpdatedAt = DateTime.Now;
        var plainJson = JsonSerializer.Serialize(store);
        return _json.SaveAsync(_paths.AssistantHistoryFile, new ProtectedHistoryFile
        {
            ProtectedPayload = WindowsSecretProtector.Protect(plainJson)
        });
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
            WebSearchCalls = rows.Sum(x => x.WebSearchCalls),
            InputTokens = rows.Sum(x => x.InputTokens),
            CachedInputTokens = rows.Sum(x => x.CachedInputTokens),
            OutputTokens = rows.Sum(x => x.OutputTokens),
            PaidToolsCostUsd = rows.Sum(x => x.PaidToolsCostUsd),
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

    public sealed record ModelRates(decimal InputUsdPerMillion, decimal CachedInputUsdPerMillion, decimal OutputUsdPerMillion);

    public static ModelRates GetModelRates(string model)
    {
        var normalized = (model ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.StartsWith("deepseek-v4-flash", StringComparison.Ordinal)) return new(0.14m, 0.014m, 0.28m);
        if (normalized.StartsWith("deepseek-v4-pro", StringComparison.Ordinal)) return new(0.435m, 0.0435m, 0.87m);
        if (normalized.StartsWith("gpt-5.6-luna", StringComparison.Ordinal)) return new(0.20m, 0.02m, 1.20m);
        if (normalized.StartsWith("gpt-5.5", StringComparison.Ordinal)) return new(5m, 0.50m, 30m);
        if (normalized.StartsWith("gpt-5.4-mini", StringComparison.Ordinal)) return new(0.75m, 0.075m, 4.50m);
        if (normalized.StartsWith("gpt-5.4-nano", StringComparison.Ordinal)) return new(0.20m, 0.02m, 1.25m);
        if (normalized.StartsWith("gpt-5.4", StringComparison.Ordinal)) return new(2.50m, 0.25m, 15m);
        if (normalized.StartsWith("gpt-5-mini", StringComparison.Ordinal)) return new(0.25m, 0.025m, 2m);
        if (normalized.StartsWith("gpt-5-nano", StringComparison.Ordinal)) return new(0.05m, 0.005m, 0.40m);
        return new(0.75m, 0.075m, 4.50m);
    }

    public static AssistantUsageRecord CalculateUsage(string model, int inputTokens, int outputTokens, decimal dollarRate,
        int webSearchCalls = 0, string provider = "OpenAI", int cachedInputTokens = 0, decimal paidToolsCostUsd = 0)
    {
        inputTokens = Math.Max(0, inputTokens);
        cachedInputTokens = Math.Clamp(cachedInputTokens, 0, inputTokens);
        outputTokens = Math.Max(0, outputTokens);
        webSearchCalls = Math.Max(0, webSearchCalls);
        paidToolsCostUsd = Math.Max(0, paidToolsCostUsd);
        var rates = GetModelRates(model);
        var uncachedInputTokens = inputTokens - cachedInputTokens;
        var webSearchCostUsd = webSearchCalls * 0.01m;
        var usd = uncachedInputTokens / 1_000_000m * rates.InputUsdPerMillion
                  + cachedInputTokens / 1_000_000m * rates.CachedInputUsdPerMillion
                  + outputTokens / 1_000_000m * rates.OutputUsdPerMillion
                  + webSearchCostUsd + paidToolsCostUsd;
        return new AssistantUsageRecord
        {
            Timestamp = DateTime.Now,
            Model = model,
            Provider = AssistantCredentialService.DisplayName(provider),
            WebSearchCalls = webSearchCalls,
            InputTokens = inputTokens,
            CachedInputTokens = cachedInputTokens,
            OutputTokens = outputTokens,
            PaidToolsCostUsd = webSearchCostUsd + paidToolsCostUsd,
            EstimatedCostUsd = usd,
            EstimatedCostBrl = usd * dollarRate
        };
    }
}
