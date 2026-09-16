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

    public static bool IsRedDay(DutyRosterMonth month, DateTime date, RedDayStore? redDayStore = null)
        => redDayStore is not null
            ? RedDayService.IsRedDay(date, redDayStore)
            : date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || month.RedDays.Contains(date.Day);

    public static Dictionary<string, string> AutoFill(
        int year,
        int month,
        IReadOnlyList<DutyRosterPerson> people,
        DutyRosterMonth current,
        IReadOnlyDictionary<string, DutyRosterMonth> allMonths,
        int startDay = 1,
        DateTime? historyStart = null,
        RedDayStore? redDayStore = null)
    {
        var assignments = new Dictionary<string, string>(current.Assignments, StringComparer.OrdinalIgnoreCase);
        if (people.Count == 0) return assignments;

        var days = DateTime.DaysInMonth(year, month);
        var historyFirstDay = historyStart?.Date ?? new DateTime(year, month, 1).AddMonths(-11);
        var monthStart = new DateTime(year, month, 1);
        var firstGeneratedDay = Math.Clamp(startDay, 1, days + 1);
        var firstGeneratedDate = monthStart.AddDays(firstGeneratedDay - 1);
        var normalIdle = people.ToDictionary(x => x.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
        var redIdle = people.ToDictionary(x => x.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
        var lastAnyDuty = people.ToDictionary(
            x => x.Key,
            x => FindLastDuty(x.Key, monthStart.AddDays(-1), allMonths),
            StringComparer.OrdinalIgnoreCase);

        // Reproduz exatamente o numero mostrado em cada celula: a contagem da categoria
        // cresce a cada dia normal/vermelho e zera somente quando a pessoa tira servico
        // naquela mesma categoria.
        for (var date = historyFirstDay; date < monthStart; date = date.AddDays(1))
        {
            var red = IsRedDayAcrossMonths(date, current, allMonths, redDayStore);
            IncrementCategoryIdle(people, red ? redIdle : normalIdle);
            if (!TryGetAssignment(allMonths, date, out var personKey)) continue;
            if (red) ResetCategoryIdle(redIdle, personKey);
            else ResetCategoryIdle(normalIdle, personKey);
        }

        // Quando o preenchimento comeca depois do dia 1, incorpora os dias anteriores
        // do proprio mes sem deixar uma escala futura bloquear datas passadas.
        for (var date = monthStart; date < firstGeneratedDate; date = date.AddDays(1))
        {
            var red = IsRedDay(current, date, redDayStore);
            IncrementCategoryIdle(people, red ? redIdle : normalIdle);
            var key = DayKey(year, month, date.Day);
            if (!assignments.TryGetValue(key, out var personKey)) continue;
            if (red) ResetCategoryIdle(redIdle, personKey);
            else ResetCategoryIdle(normalIdle, personKey);
            if (lastAnyDuty.ContainsKey(personKey)) lastAnyDuty[personKey] = date;
        }

        for (var day = firstGeneratedDay; day <= days; day++)
        {
            var dt = new DateTime(year, month, day);
            var key = DayKey(year, month, day);
            var red = IsRedDay(current, dt, redDayStore);
            var categoryIdle = red ? redIdle : normalIdle;
            var otherCategoryIdle = red ? normalIdle : redIdle;
            IncrementCategoryIdle(people, categoryIdle);

            if (assignments.TryGetValue(key, out var existingPersonKey))
            {
                ResetCategoryIdle(categoryIdle, existingPersonKey);
                if (lastAnyDuty.ContainsKey(existingPersonKey)) lastAnyDuty[existingPersonKey] = dt;
                continue;
            }

            var eligible = people
                .Where(p => !IsMarkedUnavailable(current, p.Key, year, month, day))
                .Where(p => lastAnyDuty[p.Key] is null || (dt - lastAnyDuty[p.Key]!.Value.Date).Days >= 3)
                .OrderByDescending(p => categoryIdle[p.Key])
                .ThenByDescending(p => otherCategoryIdle[p.Key])
                .ThenBy(p => lastAnyDuty[p.Key] ?? DateTime.MinValue)
                .ThenBy(p => MilitaryRankService.GetOrder(p.Rank))
                .ThenBy(p => p.Display, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var chosen = eligible.FirstOrDefault();

            if (chosen is null) continue;
            assignments[key] = chosen.Key;
            categoryIdle[chosen.Key] = 0;
            lastAnyDuty[chosen.Key] = dt;
        }
        return assignments;
    }

    private static void IncrementCategoryIdle(IReadOnlyList<DutyRosterPerson> people, Dictionary<string, int> idle)
    {
        foreach (var person in people) idle[person.Key]++;
    }

    private static void ResetCategoryIdle(Dictionary<string, int> idle, string personKey)
    {
        if (idle.ContainsKey(personKey)) idle[personKey] = 0;
    }

    private static bool TryGetAssignment(IReadOnlyDictionary<string, DutyRosterMonth> allMonths, DateTime date, out string personKey)
    {
        personKey = string.Empty;
        if (!allMonths.TryGetValue(MonthKey(date.Year, date.Month), out var month)
            || !month.Assignments.TryGetValue(DayKey(date.Year, date.Month, date.Day), out var assigned))
            return false;
        personKey = assigned;
        return true;
    }

    private static bool IsRedDayAcrossMonths(
        DateTime date,
        DutyRosterMonth current,
        IReadOnlyDictionary<string, DutyRosterMonth> allMonths,
        RedDayStore? redDayStore)
    {
        if (redDayStore is not null) return RedDayService.IsRedDay(date, redDayStore);
        return allMonths.TryGetValue(MonthKey(date.Year, date.Month), out var storedMonth)
            ? IsRedDay(storedMonth, date)
            : IsRedDay(current, date);
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
        return month.Marks.TryGetValue(key, out var mark) && IsBlockingMark(mark);
    }

    public static bool IsBlockingMark(string? mark)
    {
        var normalized = NormalizeMark(mark);
        return !string.IsNullOrWhiteSpace(normalized);
    }

    private static string NormalizeMark(string? mark)
    {
        var normalized = (mark ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant()
            .Trim();
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
