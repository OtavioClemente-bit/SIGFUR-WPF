using System.Text.Json.Serialization;

namespace SIGFUR.Wpf.Models;

public sealed class SisbolSubmissionHistoryStore
{
    public List<SisbolSubmissionHistoryEntry> Items { get; set; } = [];
}

public sealed class SisbolSubmissionHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.Now;
    public string Category { get; set; } = "Boletim";
    public string GeneralSubject { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string OpeningText { get; set; } = string.Empty;
    public string ClosingText { get; set; } = string.Empty;
    public bool IncludedClosingText { get; set; }
    public List<SisbolSubmissionMilitary> Military { get; set; } = [];

    [JsonIgnore]
    public string SentAtText => SentAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm");

    [JsonIgnore]
    public string MilitaryText => Military.Count == 0
        ? "Sem militar vinculado"
        : string.Join(", ", Military.Select(x => x.DisplayName));

    [JsonIgnore]
    public string Preview
    {
        get
        {
            var text = string.Join(' ', (OpeningText ?? string.Empty)
                .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
            return text.Length <= 150 ? text : text[..147] + "…";
        }
    }

    [JsonIgnore]
    public string CopyAllText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(OpeningText)) parts.Add(OpeningText.Trim());
            if (!string.IsNullOrWhiteSpace(ClosingText)) parts.Add(ClosingText.Trim());
            return string.Join(Environment.NewLine + Environment.NewLine, parts);
        }
    }
}

public sealed class SisbolSubmissionMilitary
{
    public int Id { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayName => string.Join(' ', new[] { Rank, Name }.Where(x => !string.IsNullOrWhiteSpace(x)));
}
