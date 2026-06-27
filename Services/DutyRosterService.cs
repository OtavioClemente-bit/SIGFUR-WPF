using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class DutyRosterService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    public DutyRosterService(AppPaths paths, JsonFileService json) { _paths = paths; _json = json; }
    public Task<DutyRosterStore?> LoadAsync() => _json.LoadAsync<DutyRosterStore>(_paths.DutyRosterFile);
    public Task SaveAsync(DutyRosterStore store) => _json.SaveAsync(_paths.DutyRosterFile, store);

    public static string MonthKey(int year, int month) => $"{year:0000}-{month:00}";
    public static string DayKey(int year, int month, int day) => $"{year:0000}-{month:00}-{day:00}";
    public static string MarkKey(string personKey, int year, int month, int day) => $"{personKey}|{DayKey(year, month, day)}";

    public static bool IsRedDay(DutyRosterMonth month, DateTime date)
        => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || month.RedDays.Contains(date.Day);

    public static Dictionary<string, string> AutoFill(
        int year,
        int month,
        IReadOnlyList<DutyRosterPerson> people,
        DutyRosterMonth current,
        IReadOnlyDictionary<string, DutyRosterMonth> allMonths,
        int startDay = 1,
        DateTime? historyStart = null)
    {
        var assignments = new Dictionary<string, string>(current.Assignments, StringComparer.OrdinalIgnoreCase);
        if (people.Count == 0) return assignments;

        var days = DateTime.DaysInMonth(year, month);
        var start = historyStart?.Date ?? new DateTime(year, month, 1).AddMonths(-11);
        var monthStart = new DateTime(year, month, 1);
        var counts = people.ToDictionary(x => x.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
        var redCounts = people.ToDictionary(x => x.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
        var last = people.ToDictionary(x => x.Key, x => FindLastDuty(x.Key, monthStart.AddDays(-1), allMonths), StringComparer.OrdinalIgnoreCase);

        foreach (var monthPair in allMonths)
        {
            foreach (var pair in monthPair.Value.Assignments)
            {
                if (!DateTime.TryParse(pair.Key, out var dt) || dt < start || dt >= monthStart || !counts.ContainsKey(pair.Value)) continue;
                counts[pair.Value]++;
                if (IsRedDay(monthPair.Value, dt)) redCounts[pair.Value]++;
            }
        }

        foreach (var pair in assignments)
        {
            if (!DateTime.TryParse(pair.Key, out var dt) || !counts.ContainsKey(pair.Value)) continue;
            counts[pair.Value]++;
            if (IsRedDay(current, dt)) redCounts[pair.Value]++;
            if (last[pair.Value] is null || dt > last[pair.Value]) last[pair.Value] = dt;
        }

        for (var day = Math.Max(1, startDay); day <= days; day++)
        {
            var dt = new DateTime(year, month, day);
            var key = DayKey(year, month, day);
            if (assignments.ContainsKey(key)) continue;
            var red = IsRedDay(current, dt);
            var eligible = people
                .Where(p => !IsMarkedUnavailable(current, p.Key, year, month, day))
                .Where(p => last[p.Key] is null || (dt - last[p.Key]!.Value.Date).Days >= 3)
                .OrderBy(p => red ? redCounts[p.Key] : counts[p.Key])
                .ThenBy(p => counts[p.Key])
                .ThenBy(p => last[p.Key] ?? DateTime.MinValue)
                .ThenBy(p => MilitaryRankService.GetOrder(p.Rank))
                .ThenBy(p => p.Display, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (eligible.Count == 0)
                eligible = people
                    .Where(p => !IsMarkedUnavailable(current, p.Key, year, month, day))
                    .OrderBy(p => counts[p.Key])
                    .ThenBy(p => last[p.Key] ?? DateTime.MinValue)
                    .ThenBy(p => p.Display, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            var chosen = eligible.FirstOrDefault();
            if (chosen is null) continue;
            assignments[key] = chosen.Key;
            counts[chosen.Key]++;
            if (red) redCounts[chosen.Key]++;
            last[chosen.Key] = dt;
        }
        return assignments;
    }

    public static DateTime GetNextOpenDate(int year, int month, DutyRosterMonth current)
    {
        var days = DateTime.DaysInMonth(year, month);
        var first = new DateTime(year, month, 1);
        var startDay = year == DateTime.Today.Year && month == DateTime.Today.Month
            ? Math.Max(1, DateTime.Today.Day)
            : 1;
        for (var day = startDay; day <= days; day++)
        {
            var key = DayKey(year, month, day);
            if (!current.Assignments.ContainsKey(key)) return new DateTime(year, month, day);
        }
        return first.AddMonths(1);
    }

    public static DateTime CounterStart(int year, int month, int months)
        => new DateTime(year, month, 1).AddMonths(-Math.Max(1, months) + 1);

    public static List<DateTime> GetDuties(string personKey, IReadOnlyDictionary<string, DutyRosterMonth> months, DateTime? from = null, DateTime? until = null)
    {
        var result = new List<DateTime>();
        foreach (var month in months.Values)
        foreach (var pair in month.Assignments)
        {
            if (!pair.Value.Equals(personKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateTime.TryParse(pair.Key, out var dt)) continue;
            if (from is not null && dt.Date < from.Value.Date) continue;
            if (until is not null && dt.Date > until.Value.Date) continue;
            result.Add(dt.Date);
        }
        return result.OrderBy(x => x).ToList();
    }

    private static bool IsMarkedUnavailable(DutyRosterMonth month, string personKey, int year, int monthValue, int day)
    {
        var key = MarkKey(personKey, year, monthValue, day);
        return month.Marks.TryGetValue(key, out var mark) && !string.IsNullOrWhiteSpace(mark);
    }

    public static DateTime? FindLastDuty(string personKey, DateTime until, IReadOnlyDictionary<string, DutyRosterMonth> months)
    {
        DateTime? last = null;
        foreach (var month in months.Values)
        foreach (var pair in month.Assignments)
        {
            if (!pair.Value.Equals(personKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateTime.TryParse(pair.Key, out var dt) || dt > until) continue;
            if (last is null || dt > last) last = dt;
        }
        return last;
    }
}
