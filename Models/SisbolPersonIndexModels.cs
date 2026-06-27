using System.Text.Json.Serialization;

namespace SIGFUR.Wpf.Models;

public sealed class SisbolPersonIndexItem
{
    public int Id { get; set; }
    public string SourcePdfPath { get; set; } = string.Empty;
    public string SourcePdfHash { get; set; } = string.Empty;
    public DateTime? IndexStartDate { get; set; }
    public DateTime? IndexEndDate { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string PersonName { get; set; } = string.Empty;
    public int? MilitaryId { get; set; }
    public string LinkedFullName { get; set; } = string.Empty;
    public string LinkedWarName { get; set; } = string.Empty;
    public string MainSubject { get; set; } = string.Empty;
    public string SubSubject { get; set; } = string.Empty;
    public string SubjectNote { get; set; } = string.Empty;
    public string BulletinType { get; set; } = "BI";
    public string BulletinNumber { get; set; } = string.Empty;
    public DateTime? BulletinDate { get; set; }
    public int? BulletinPage { get; set; }
    public string NoteNumber { get; set; } = string.Empty;
    public string SisbolUser { get; set; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore] public string AggregatedPeople { get; set; } = string.Empty;
    [JsonIgnore] public string AggregatedSearchPerson { get; set; } = string.Empty;
    [JsonIgnore] public int AggregatedPersonCount { get; set; }
    [JsonIgnore] public int AggregatedLinkedCount { get; set; }

    [JsonIgnore] public string DateText => BulletinDate?.ToString("dd/MM/yyyy") ?? "—";
    [JsonIgnore] public string DisplayPersonName => string.IsNullOrWhiteSpace(LinkedFullName) ? PersonName : LinkedFullName;
    [JsonIgnore] public string DisplayWarName => LinkedWarName;
    [JsonIgnore] public string PeopleDisplay => !string.IsNullOrWhiteSpace(AggregatedPeople) ? AggregatedPeople : DisplayPersonName;
    [JsonIgnore] public string PeopleCountText => AggregatedPersonCount > 1 ? AggregatedPersonCount.ToString(CultureInfo.InvariantCulture) : "1";
    [JsonIgnore] public string BulletinDisplay => string.IsNullOrWhiteSpace(BulletinNumber) ? "—" : $"BI {BulletinNumber}";
    [JsonIgnore] public string PageText => BulletinPage?.ToString(CultureInfo.InvariantCulture) ?? "—";
    [JsonIgnore] public string MainSubjectDisplay => string.IsNullOrWhiteSpace(MainSubject) ? "Assunto não identificado" : MainSubject;
    [JsonIgnore] public string NoteDisplay => string.IsNullOrWhiteSpace(SubSubject) ? "—" : SubSubject;
    [JsonIgnore] public string OpenSearchTerm => FirstNonEmpty(AggregatedSearchPerson, PersonName, DisplayPersonName);
    [JsonIgnore] public string LinkedText => AggregatedPersonCount > 1
        ? $"{AggregatedLinkedCount}/{AggregatedPersonCount}"
        : MilitaryId.HasValue ? "Sim" : "Não";
    [JsonIgnore] public string SourceFileName => string.IsNullOrWhiteSpace(SourcePdfPath) ? "—" : Path.GetFileName(SourcePdfPath);
    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    [JsonIgnore] public string DisplaySubjectNote
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SubjectNote)) return SubjectNote;
            if (!string.IsNullOrWhiteSpace(MainSubject) && !string.IsNullOrWhiteSpace(SubSubject)) return $"{MainSubject} — {SubSubject}";
            return string.IsNullOrWhiteSpace(MainSubject) ? "Assunto não identificado" : MainSubject;
        }
    }
}

public sealed class SisbolPersonIndexImportResult
{
    public string SourcePdfPath { get; set; } = string.Empty;
    public string SourcePdfHash { get; set; } = string.Empty;
    public DateTime? IndexStartDate { get; set; }
    public DateTime? IndexEndDate { get; set; }
    public int Imported { get; set; }
    public int Linked { get; set; }
    public int Unlinked => Math.Max(0, Imported - Linked);
    public List<string> Errors { get; } = [];
}

public sealed class SisbolPersonIndexQuery
{
    public string Search { get; set; } = string.Empty;
    public string Year { get; set; } = "Todos";
    public string Month { get; set; } = "Todos";
    public string LinkFilter { get; set; } = "Todos";
    public string User { get; set; } = "Todos";
    public string Subject { get; set; } = "Todos";
    public string Person { get; set; } = "Todos";
    public string Note { get; set; } = "Todos";
    public string SubjectOrNote { get; set; } = string.Empty;
}

public sealed class SisbolPersonIndexPersonOption
{
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string LinkedFullName { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public int Count { get; set; }
    public string FullName => string.IsNullOrWhiteSpace(LinkedFullName) ? Name : LinkedFullName;
    public string Display
    {
        get
        {
            var prefix = string.IsNullOrWhiteSpace(Rank) ? string.Empty : MilitaryRankService.ShortName(Rank) + " ";
            var value = (prefix + FullName).Trim();
            return Count > 0 ? $"{value} ({Count})" : value;
        }
    }
    public string SearchText => string.Join(' ', Rank, Name, LinkedFullName, WarName, Count.ToString(CultureInfo.InvariantCulture));
    public override string ToString() => Display;
}

public sealed class SisbolPersonIndexSummary
{
    public int TotalRecords { get; set; }
    public int PeopleCount { get; set; }
    public int LinkedCount { get; set; }
    public int UnlinkedCount { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string PeriodText
    {
        get
        {
            if (StartDate is null && EndDate is null) return "Sem índice importado";
            if (StartDate is not null && EndDate is not null) return $"{StartDate:dd/MM/yyyy} a {EndDate:dd/MM/yyyy}";
            return (StartDate ?? EndDate)?.ToString("dd/MM/yyyy") ?? "—";
        }
    }
}

public sealed class SisbolPersonIndexDownloadResult
{
    public string FilePath { get; set; } = string.Empty;
    public bool Downloaded => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);
}
