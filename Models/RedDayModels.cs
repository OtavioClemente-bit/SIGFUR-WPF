namespace SIGFUR.Wpf.Models;

public enum RedDayKind
{
    None,
    Weekend,
    Holiday,
    Manual,
    DutyRoster
}

public sealed class RedDayStore
{
    public Dictionary<string, ManualRedDayRecord> ManualDays { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ManualRedDayRecord
{
    public string Date { get; set; } = string.Empty;
    public bool IsRed { get; set; } = true;
    public string Reason { get; set; } = string.Empty;
    public string Source { get; set; } = "Calendario";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class RedDayInfo
{
    public bool IsRedDay { get; set; }
    public RedDayKind Kind { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string TooltipText => Kind switch
    {
        RedDayKind.Holiday => "Feriado: " + Reason,
        RedDayKind.Weekend => Reason,
        RedDayKind.Manual or RedDayKind.DutyRoster => string.IsNullOrWhiteSpace(Reason) ? "Dia vermelho" : Reason,
        _ => string.Empty
    };
}
