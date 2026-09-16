using System.Globalization;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class RedDayService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly object _sync = new();
    private RedDayStore? _cache;

    public RedDayService(AppPaths paths, JsonFileService json)
    {
        _paths = paths;
        _json = json;
    }

    public async Task<RedDayStore> LoadAsync()
    {
        var store = await _json.LoadAsync<RedDayStore>(_paths.RedDaysFile) ?? new RedDayStore();
        store.ManualDays ??= new(StringComparer.OrdinalIgnoreCase);
        lock (_sync) _cache = store;
        return store;
    }

    public async Task SaveAsync(RedDayStore store)
    {
        store.ManualDays ??= new(StringComparer.OrdinalIgnoreCase);
        await _json.SaveAsync(_paths.RedDaysFile, store);
        lock (_sync) _cache = store;
    }

    public RedDayInfo GetInfo(DateTime date)
        => GetInfo(date, EnsureLoaded());

    public static bool IsRedDay(DateTime date, RedDayStore? store = null)
        => GetInfo(date, store).IsRedDay;

    public static string GetRedDayReason(DateTime date, RedDayStore? store = null)
        => GetInfo(date, store).TooltipText;

    public static RedDayInfo GetInfo(DateTime date, RedDayStore? store)
    {
        date = date.Date;
        if (store?.ManualDays is not null &&
            store.ManualDays.TryGetValue(Key(date), out var manual) &&
            manual.IsRed)
        {
            var source = manual.Source?.Trim();
            return new RedDayInfo
            {
                IsRedDay = true,
                Kind = source?.Equals("Escala Sgt de Dia", StringComparison.OrdinalIgnoreCase) == true ? RedDayKind.DutyRoster : RedDayKind.Manual,
                Reason = string.IsNullOrWhiteSpace(manual.Reason) ? "Dia vermelho" : manual.Reason.Trim()
            };
        }

        if (TryGetHolidayName(date, out var holiday))
            return new RedDayInfo { IsRedDay = true, Kind = RedDayKind.Holiday, Reason = holiday };

        return date.DayOfWeek switch
        {
            DayOfWeek.Saturday => new RedDayInfo { IsRedDay = true, Kind = RedDayKind.Weekend, Reason = "Sábado" },
            DayOfWeek.Sunday => new RedDayInfo { IsRedDay = true, Kind = RedDayKind.Weekend, Reason = "Domingo" },
            _ => new RedDayInfo()
        };
    }

    public async Task SetManualRedDayAsync(DateTime date, bool isRed, string? reason, string source = "Calendario")
    {
        var store = await LoadAsync();
        var key = Key(date);
        if (!isRed)
        {
            store.ManualDays.Remove(key);
        }
        else
        {
            store.ManualDays[key] = new ManualRedDayRecord
            {
                Date = key,
                IsRed = true,
                Reason = string.IsNullOrWhiteSpace(reason) ? "Dia vermelho" : reason.Trim(),
                Source = string.IsNullOrWhiteSpace(source) ? "Calendario" : source.Trim(),
                UpdatedAt = DateTime.Now
            };
        }
        await SaveAsync(store);
    }

    public async Task MigrateDutyRosterRedDaysAsync(DutyRosterStore store)
    {
        if (store.Months.Count == 0) return;
        var redDays = await LoadAsync();
        var changed = false;
        foreach (var pair in store.Months)
        {
            if (pair.Value.RedDays.Count == 0) continue;
            if (!DateTime.TryParseExact(pair.Key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthStart))
                continue;

            foreach (var day in pair.Value.RedDays)
            {
                if (day < 1 || day > DateTime.DaysInMonth(monthStart.Year, monthStart.Month)) continue;
                var date = new DateTime(monthStart.Year, monthStart.Month, day);
                var key = Key(date);
                if (redDays.ManualDays.ContainsKey(key)) continue;
                redDays.ManualDays[key] = new ManualRedDayRecord
                {
                    Date = key,
                    IsRed = true,
                    Reason = "Escala Sgt de Dia",
                    Source = "Escala Sgt de Dia",
                    UpdatedAt = DateTime.Now
                };
                changed = true;
            }
        }

        if (changed) await SaveAsync(redDays);
    }

    public static bool TryGetHolidayName(DateTime date, out string holidayName)
    {
        holidayName = (date.Month, date.Day) switch
        {
            (1, 1) => "Confraternização Universal",
            (4, 21) => "Tiradentes",
            (5, 1) => "Dia do Trabalho",
            (9, 7) => "Independência do Brasil",
            (10, 12) => "Nossa Senhora Aparecida",
            (11, 2) => "Finados",
            (11, 15) => "Proclamação da República",
            (11, 20) => "Consciência Negra",
            (12, 25) => "Natal",
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(holidayName)) return true;

        var easter = EasterSunday(date.Year);
        if (date.Date == easter.AddDays(-48).Date) { holidayName = "Carnaval"; return true; }
        if (date.Date == easter.AddDays(-47).Date) { holidayName = "Carnaval"; return true; }
        if (date.Date == easter.AddDays(-2).Date) { holidayName = "Sexta-feira Santa"; return true; }
        if (date.Date == easter.AddDays(60).Date) { holidayName = "Corpus Christi"; return true; }
        return false;
    }

    private RedDayStore EnsureLoaded()
    {
        lock (_sync)
        {
            if (_cache is not null) return _cache;
        }

        try
        {
            if (!File.Exists(_paths.RedDaysFile)) return new RedDayStore();
            var json = File.ReadAllText(_paths.RedDaysFile);
            var store = System.Text.Json.JsonSerializer.Deserialize<RedDayStore>(json) ?? new RedDayStore();
            store.ManualDays ??= new(StringComparer.OrdinalIgnoreCase);
            lock (_sync) _cache = store;
            return store;
        }
        catch
        {
            return new RedDayStore();
        }
    }

    private static string Key(DateTime date) => date.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }
}
