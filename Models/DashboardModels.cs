using System.Collections.ObjectModel;
using System.Globalization;

namespace SIGFUR.Wpf.Models;

public sealed class DashboardSnapshot
{
    public int ActiveMilitaryCount { get; set; }
    public int LicensedTransferredCount { get; set; }
    public int ReminderTotal { get; set; }
    public int ReminderOverdue { get; set; }
    public int ReminderToday { get; set; }
    public int ReminderUpcoming { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public string DatabaseSize { get; set; } = "—";
    public string DatabaseStatus { get; set; } = string.Empty;
    public string LastBackup { get; set; } = "—";
    public int BackupCount { get; set; }
    public string Version { get; set; } = "v6.1.11";
    public string CalendarMonthTitle { get; set; } = string.Empty;
    public string CalendarSummary { get; set; } = string.Empty;
    public ObservableCollection<ReminderItem> Reminders { get; } = [];
    public ObservableCollection<MilitaryItem> MissingTransportAid { get; } = [];
    public ObservableCollection<BulletinItem> Bulletins { get; } = [];
    public ObservableCollection<FinancialAlertItem> FinancialAlerts { get; } = [];
    public ObservableCollection<OperationalIssueItem> OperationalIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> PaymentIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> PaymentGeneralIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> PaymentVacationIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> BulletinIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> BulletinMissingSisbolIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> RegisterIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> RegisterMissingTransportAidIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> RegisterMissingAddressIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> RegisterMissingCpfIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> RegisterBankIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> RegisterMissingPhotoIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> RegisterIdentityIssues { get; } = [];
    public ObservableCollection<OperationalIssueItem> RoutineIssues { get; } = [];
    public int OperationalIssueTotal { get; set; }
    public int OperationalIssueCritical { get; set; }
    public int OperationalIssueAttention { get; set; }
    public string OperationalIssueSummary { get; set; } = "Nenhuma pendência crítica identificada.";
    public ObservableCollection<BirthdayItem> Birthdays { get; } = [];
    public ObservableCollection<RankSummaryItem> RankSummary { get; } = [];
    public ObservableCollection<MilitaryItem> Military { get; } = [];
    public ObservableCollection<CalendarDayItem> ReminderCalendarDays { get; } = [];
}

public sealed class CalendarDayItem
{
    public DateTime Date { get; set; }
    public string DayNumber { get; set; } = string.Empty;
    public string DayMonthText => Date.ToString("dd/MM");
    public bool IsCurrentMonth { get; set; }
    public bool IsToday { get; set; }
    public bool IsWeekend { get; set; }
    public bool IsRedDay { get; set; }
    public string RedDayReason { get; set; } = string.Empty;
    public bool HasReminder { get; set; }
    public bool HasPaymentReminder { get; set; }
    public bool HasService { get; set; }
    public bool HasCustomEvent { get; set; }
    public int ReminderCount { get; set; }
    public int PaymentReminderCount { get; set; }
    public int ServiceCount { get; set; }
    public int CustomEventCount { get; set; }
    public int TotalCount => ReminderCount + PaymentReminderCount + ServiceCount + CustomEventCount;
    public string Tooltip { get; set; } = string.Empty;
}


public sealed class ReminderItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Date { get; set; } = "—";
    public string Status { get; set; } = string.Empty;
    public string Priority { get; set; } = "Normal";
    public bool Completed { get; set; }
    public string Body { get; set; } = string.Empty;
    public string Recurrence { get; set; } = "Nenhuma";
    public int Order { get; set; }
    public bool Urgent { get; set; }
    public string OrderText => Completed ? "✓" : Order > 0 ? Order.ToString("00", CultureInfo.InvariantCulture) : "—";
    public string UrgentText => Urgent ? "URGENTE" : string.Empty;
    public bool IsTopThree => !Completed && Order is >= 1 and <= 3;
    public bool IsNextThree => !Completed && Order is >= 4 and <= 6;
}

public sealed class MilitaryItem
{
    public int Id { get; set; }
    public string Rank { get; set; } = "—";
    public string Name { get; set; } = "—";
    public string WarName { get; set; } = "—";
    public string Cpf { get; set; } = string.Empty;
    public string FormationYear { get; set; } = "—";
    public string DisplayName => string.IsNullOrWhiteSpace(WarName) || WarName == "—" ? Name : $"{Name} — {WarName}";
    public string NameBeforeWar => SplitDisplayName().Before;
    public string NameWarBold => SplitDisplayName().War;
    public string NameAfterWar => SplitDisplayName().After;

    private (string Before, string War, string After) SplitDisplayName()
    {
        var fullName = (Name ?? string.Empty).Trim();
        var warName = (WarName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(warName) || warName == "—") return (fullName, string.Empty, string.Empty);
        var index = fullName.IndexOf(warName, StringComparison.CurrentCultureIgnoreCase);
        return index >= 0
            ? (fullName[..index], fullName.Substring(index, warName.Length), fullName[(index + warName.Length)..])
            : (fullName + " — ", warName, string.Empty);
    }
}

public sealed class BulletinItem
{
    public string Type { get; set; } = string.Empty;
    public string Number { get; set; } = "—";
    public string Date { get; set; } = "—";
    public string File { get; set; } = "—";
    public string Path { get; set; } = string.Empty;
}


public sealed class OperationalIssueItem
{
    public string Severity { get; set; } = "Normal";
    public string Category { get; set; } = "Rotina";
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string SortKey { get; set; } = string.Empty;
    public string PersonRank { get; set; } = string.Empty;
    public string PersonName { get; set; } = string.Empty;
    public string PersonWarName { get; set; } = string.Empty;
}

public sealed class FinancialAlertItem
{
    public string Id { get; set; } = string.Empty;
    public string Deadline { get; set; } = "—";
    public string Type { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Manual { get; set; }
}

public sealed class BirthdayItem
{
    public int MilitaryId { get; set; }
    public string Day { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public bool IsToday { get; set; }
    public bool Confirmed { get; set; }
    public string NameBeforeWar => SplitDisplayName().Before;
    public string NameWarBold => SplitDisplayName().War;
    public string NameAfterWar => SplitDisplayName().After;

    private (string Before, string War, string After) SplitDisplayName()
    {
        var fullName = (Name ?? string.Empty).Trim();
        var warName = (WarName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(warName)) return (fullName, string.Empty, string.Empty);
        var index = fullName.IndexOf(warName, StringComparison.CurrentCultureIgnoreCase);
        return index >= 0
            ? (fullName[..index], fullName.Substring(index, warName.Length), fullName[(index + warName.Length)..])
            : (fullName + " — ", warName, string.Empty);
    }
}

public sealed class RankSummaryItem
{
    public string Rank { get; set; } = string.Empty;
    public int Count { get; set; }
    public ObservableCollection<FormationYearItem> Years { get; } = [];
}

public sealed class FormationYearItem
{
    public string Year { get; set; } = "—";
    public int Count { get; set; }
}
