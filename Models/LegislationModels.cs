using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class LegislationDocument
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    public int PageCount { get; set; }
    public DateTime IndexedAt { get; set; }
    public string IndexedAtText => IndexedAt == default ? "—" : IndexedAt.ToString("dd/MM/yyyy HH:mm");
    public string SizeText => MilitaryFormatting.FormatFileSize(Size);
}

public sealed class LegislationSearchHit
{
    public long DocumentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int Page { get; set; }
    public double Score { get; set; }
    public string Snippet { get; set; } = string.Empty;
    public string Reference => $"{Title}, p. {Page}";
}

public sealed class LegislationStats
{
    public int Documents { get; set; }
    public int Pages { get; set; }
    public DateTime? LastIndexed { get; set; }
    public string Display => $"{Documents:N0} documento(s) • {Pages:N0} página(s)" + (LastIndexed is null ? string.Empty : $" • atualizado {LastIndexed:dd/MM/yyyy HH:mm}");
}
