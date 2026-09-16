namespace SIGFUR.Wpf.Models;

public sealed class GratificationProcessStore
{
    public List<GratificationProcessRecord> Processes { get; set; } = [];
}

public sealed class GratificationProcessRecord
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string Status { get; set; } = "Rascunho";
    public string SpedMessage { get; set; } = string.Empty;
    public string SpedUrl { get; set; } = string.Empty;
    public List<string> ReviewScreenshots { get; set; } = [];
    public Dictionary<string, int> EffectiveByRank { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public GratificationSettings Settings { get; set; } = new();

    public string UpdatedText => UpdatedAt.ToString("dd/MM/yyyy HH:mm");
    public string DeletedText => IsDeleted ? "Excluído" : "Ativo";
}

public sealed class SpedAutomationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public List<string> ScreenshotPaths { get; init; } = [];
}
