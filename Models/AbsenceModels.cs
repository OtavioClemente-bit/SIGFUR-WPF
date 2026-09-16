using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class AbsenceOccurrence
{
    public long Id { get; set; }
    public int MilitaryId { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Today;
    public string Time { get; set; } = string.Empty;
    public string Type { get; set; } = "ATRASO";
    public int Minutes { get; set; }
    public bool Justified { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Measure { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string DateText => Date.ToString("dd/MM/yyyy");
    public string JustifiedText => Justified ? "Justificada" : "Não justificada";
    public string ShortRank => MilitaryRankService.ShortName(Rank);
    public string DurationText => Type.Equals("ATRASO", StringComparison.OrdinalIgnoreCase) && Minutes > 0 ? $"{Minutes} min" : "—";
}

public sealed class AbsenceSummary
{
    public int Total { get; set; }
    public int Absences { get; set; }
    public int Delays { get; set; }
    public int Unjustified { get; set; }
    public int DelayMinutes { get; set; }
}

public sealed class AbsenceClosure
{
    public int Year { get; set; }
    public int Month { get; set; }
    public DateTime ClosedAt { get; set; } = DateTime.Now;
    public string Note { get; set; } = string.Empty;
    public AbsenceSummary Summary { get; set; } = new();
    public string ClosedAtText => ClosedAt.ToString("dd/MM/yyyy HH:mm");
}
