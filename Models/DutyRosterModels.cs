using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class DutyRosterStore
{
    public List<int> SelectedMilitaryIds { get; set; } = [];
    public bool SelectionInitialized { get; set; }
    public List<string> ExtraPeople { get; set; } = [];
    public List<int> Order { get; set; } = [];
    public Dictionary<string, DutyRosterMonth> Months { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DutyMarkStyle> MarkStyles { get; set; } = DutyMarkStyle.Defaults();
    public int CounterStartYear { get; set; } = DateTime.Today.Year;
    public int CounterStartMonth { get; set; } = 1;
    public int CounterMonths { get; set; } = 12;
}

public sealed class DutyRosterMonth
{
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<int> RedDays { get; set; } = [];
    public Dictionary<string, string> Marks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DutyRosterPerson
{
    public int MilitaryId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public bool IsExtra { get; set; }
    public string ShortRank => MilitaryRankService.ShortName(Rank);
    public string Display => string.IsNullOrWhiteSpace(ShortRank) || ShortRank == "—"
        ? Name
        : $"{ShortRank} — {NameHighlightHelper.PlainDisplay(Name, WarName)}";
}

public sealed class DutyMarkStyle
{
    public string Name { get; set; } = string.Empty;
    public string Background { get; set; } = "#EEEEEE";
    public string Foreground { get; set; } = "#424242";

    public static Dictionary<string, DutyMarkStyle> Defaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["FÉRIAS"] = new() { Name = "FÉRIAS", Background = "#FFE082", Foreground = "#5D4037" },
        ["CURSO/ESTÁGIO"] = new() { Name = "CURSO/ESTÁGIO", Background = "#B3E5FC", Foreground = "#01579B" },
        ["DISPENSA"] = new() { Name = "DISPENSA", Background = "#E1BEE7", Foreground = "#4A148C" },
        ["BAIXADO"] = new() { Name = "BAIXADO", Background = "#FFCCBC", Foreground = "#BF360C" },
        ["MISSÃO"] = new() { Name = "MISSÃO", Background = "#C8E6C9", Foreground = "#1B5E20" },
        ["OUTROS"] = new() { Name = "OUTROS", Background = "#EEEEEE", Foreground = "#424242" }
    };
}

public sealed class DutyRosterCounter
{
    public string PersonKey { get; set; } = string.Empty;
    public string Person { get; set; } = string.Empty;
    public int Total { get; set; }
    public int RedDays { get; set; }
    public int WeekDays => Math.Max(0, Total - RedDays);
    public DateTime? LastDuty { get; set; }
    public string LastDutyText => LastDuty?.ToString("dd/MM/yyyy") ?? "Nunca";
    public int DaysOff { get; set; }
    public string DaysOffText => $"{DaysOff:N0} d";
    public bool IsEligible { get; set; }
    public string EligibilityText => IsEligible ? "LIBERADO" : "48 H";
    public int Priority { get; set; }
    public string PriorityText => Priority <= 0 ? "—" : $"{Priority}º";
    public bool IsNextCandidate { get; set; }
}
