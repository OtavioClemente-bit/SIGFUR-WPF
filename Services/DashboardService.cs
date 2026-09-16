using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Dashboard 100% C#: lê o SQLite oficial e os JSONs do AppData sem depender da ponte Python.
/// A ponte permanece somente para abrir módulos que ainda não foram migrados.
/// </summary>
public sealed class DashboardService
{
    private readonly DatabaseSafetyService _database;
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly RedDayService _redDays;
    private readonly DutyRosterService _dutyRoster;

    public DashboardService(DatabaseSafetyService database, AppPaths paths, JsonFileService json, RedDayService redDays)
    {
        _database = database;
        _paths = paths;
        _json = json;
        _redDays = redDays;
        _dutyRoster = new DutyRosterService(paths, json);
    }

    public long GetChangeStamp()
    {
        // Só metadados: evita reler banco e JSONs para descobrir que nada mudou.
        // O hash não precisa sobreviver entre execuções; serve apenas para comparação
        // durante a sessão atual.
        unchecked
        {
            long stamp = 1469598103934665603L;
            foreach (var path in new[]
            {
                _paths.DatabaseFile,
                _paths.RemindersFile,
                _paths.PaymentRemindersFile,
                _paths.CalendarEventsFile,
                _paths.BirthdayConferenceFile,
                _paths.BulletinIndexFile,
                _paths.FurrielIndexFile,
                _paths.RedDaysFile,
                _paths.DutyRosterFile
            })
            {
                stamp = Mix(stamp, FileStamp(path));
            }

            stamp = Mix(stamp, DirectoryStamp(_paths.BackupsDirectory));
            return stamp;
        }
    }

    private static long FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0L;
        }
        catch { return 0L; }
    }

    private static long DirectoryStamp(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks : 0L;
        }
        catch { return 0L; }
    }

    private static long Mix(long current, long value)
        => (current ^ value) * 1099511628211L;

    public async Task<DashboardSnapshot> LoadAsync(DateTime? calendarMonth = null, CancellationToken cancellationToken = default)
    {
        var inspection = await _database.EnsureAvailableAsync(cancellationToken);
        var snapshot = new DashboardSnapshot
        {
            DatabasePath = inspection.Path,
            DatabaseSize = inspection.SizeText,
            DatabaseStatus = inspection.Exists
                ? inspection.IsValid
                    ? $"{Math.Max(0, inspection.MilitaryCount)} militar(es)"
                    : $"Banco encontrado com falha de integridade: {inspection.Error}"
                : "Banco oficial não encontrado",
            Version = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "5.0.18")
        };

        List<MilitaryRecord> records = inspection.IsValid && inspection.HasMilitaryTable
            ? await Task.Run(() => ReadMilitaryRecords(inspection.Path), cancellationToken)
            : new List<MilitaryRecord>();

        snapshot.ActiveMilitaryCount = records.Count;
        snapshot.LicensedTransferredCount = inspection.IsValid
            ? await Task.Run(() => ReadLicensedCount(inspection.Path), cancellationToken)
            : 0;

        foreach (var record in records)
            snapshot.Military.Add(ToMilitaryItem(record));

        foreach (var record in records.Where(x => !ReceivesTransport(x)).OrderBy(RankSort).ThenBy(x => Normalize(x.Name)))
            snapshot.MissingTransportAid.Add(ToMilitaryItem(record));

        await LoadRemindersAsync(snapshot);
        await LoadBulletinsAsync(snapshot);
        await LoadFinancialAlertsAsync(snapshot);
        await LoadBirthdaysAsync(snapshot, records);
        var calendarEvents = await LoadCalendarEventsAsync();
        var dutyRoster = await _dutyRoster.LoadAsync();
        if (dutyRoster is not null)
            await _redDays.MigrateDutyRosterRedDaysAsync(dutyRoster);
        var redDayStore = await _redDays.LoadAsync();
        BuildReminderCalendar(snapshot, calendarEvents, redDayStore, calendarMonth ?? DateTime.Today, records, dutyRoster);
        LoadRankSummary(snapshot, records);
        LoadBackupInformation(snapshot);
        BuildOperationalIssues(snapshot, records);
        return snapshot;
    }

    private List<MilitaryRecord> ReadMilitaryRecords(string path)
    {
        var result = new List<MilitaryRecord>();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 8
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM militares;";
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(i => reader.GetName(i), i => i, StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            result.Add(new MilitaryRecord
            {
                Id = Int(reader, columns, "id", "militar_id"),
                Rank = CanonRank(Str(reader, columns, "posto", "posto_grad", "posto_graduacao")),
                Name = Str(reader, columns, "nome", "nome_completo", fallback: "—"),
                WarName = Str(reader, columns, "nome_guerra", "guerra", fallback: "—"),
                Cpf = Str(reader, columns, "cpf"),
                PrecCp = Str(reader, columns, "prec_cp", "preccp", "prec"),
                MilitaryId = Str(reader, columns, "idt", "identidade", "id_militar"),
                Bank = Str(reader, columns, "banco"),
                Agency = Str(reader, columns, "agencia"),
                Account = Str(reader, columns, "conta"),
                PhotoPath = Str(reader, columns, "foto", "foto_path", "photo_path"),
                Address = Str(reader, columns, "endereco", "endereco_residencial", "logradouro", fallback: ""),
                FormationYear = Str(reader, columns, "ano", "ano_formacao", "turma", fallback: "—"),
                BirthDate = Str(reader, columns, "data_nascimento", "nascimento", "dt_nascimento", "data_nasc"),
                ReceivesTransportRaw = Str(reader, columns, "recebe_aux_transporte", "recebe_aux", "recebe_at"),
                TransportValueRaw = Str(reader, columns, "valor_aux_transporte", "aux_transporte_valor", "aux_valor")
            });
        }
        return result;
    }

    private static int ReadLicensedCount(string path)
    {
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='lt_militares');";
            if (Convert.ToInt64(exists.ExecuteScalar() ?? 0) == 0) return 0;

            using var columns = connection.CreateCommand();
            columns.CommandText = "PRAGMA table_info(lt_militares);";
            var hasVisible = false;
            using (var reader = columns.ExecuteReader())
            {
                while (reader.Read())
                    if (string.Equals(reader["name"]?.ToString(), "visivel", StringComparison.OrdinalIgnoreCase)) hasVisible = true;
            }

            using var count = connection.CreateCommand();
            count.CommandText = hasVisible
                ? "SELECT COUNT(*) FROM lt_militares WHERE visivel IS NULL OR visivel=1;"
                : "SELECT COUNT(*) FROM lt_militares;";
            return Convert.ToInt32(count.ExecuteScalar() ?? 0);
        }
        catch { return 0; }
    }

    private async Task LoadRemindersAsync(DashboardSnapshot snapshot)
    {
        var root = await _json.LoadNodeAsync(_paths.RemindersFile);
        var items = root switch
        {
            JsonArray array => array,
            JsonObject obj when obj["items"] is JsonArray array => array,
            _ => new JsonArray()
        };
        var today = DateTime.Today;
        var reminders = new List<ReminderItem>();
        var index = 0;

        foreach (var node in items.OfType<JsonObject>())
        {
            var completed = Bool(node, "concluido") || Bool(node, "completed");
            var date = ParseDate(Str(node, "data", Str(node, "date")));
            string status;
            if (completed) status = "Concluído";
            else if (date is null) status = "Sem data";
            else if (date.Value.Date < today) { status = "Atrasado"; snapshot.ReminderOverdue++; }
            else if (date.Value.Date == today) { status = "Hoje"; snapshot.ReminderToday++; }
            else
            {
                status = "Pendente";
                if ((date.Value.Date - today).Days is > 0 and <= 2) snapshot.ReminderUpcoming++;
            }
            reminders.Add(new ReminderItem
            {
                Id = Str(node, "id", (index++).ToString()),
                Title = Str(node, "titulo", Str(node, "title")),
                Date = date?.ToString("dd/MM/yyyy") ?? "—",
                Status = status,
                Priority = ReminderRules.AutoPriority(date, Str(node, "prioridade", Str(node, "priority", "Normal"))),
                Completed = completed,
                Body = Str(node, "corpo", Str(node, "body")),
                Recurrence = Str(node, "recorrencia", "Nenhuma"),
                Order = JsonInt(node, "ordem"),
                Urgent = Bool(node, "urgente")
            });
        }

        snapshot.ReminderTotal = reminders.Count(x => !x.Completed);
        foreach (var item in reminders.OrderBy(x => x.Completed).ThenBy(x => x.Completed ? int.MaxValue : x.Order).ThenBy(x => Normalize(x.Title)))
            snapshot.Reminders.Add(item);
    }

    private async Task LoadBirthdaysAsync(DashboardSnapshot snapshot, IReadOnlyList<MilitaryRecord> records)
    {
        var conference = await _json.LoadNodeAsync(_paths.BirthdayConferenceFile) as JsonObject;
        var monthKey = DateTime.Today.ToString("yyyy-MM");
        var bucket = conference?[monthKey] as JsonObject;

        foreach (var record in records)
        {
            var birth = ParseDate(record.BirthDate);
            if (birth is null || birth.Value.Month != DateTime.Today.Month) continue;
            var confirmedNode = bucket?[record.Id.ToString()];
            var confirmed = confirmedNode switch
            {
                JsonObject obj => Bool(obj, "conferido"),
                JsonValue value when value.TryGetValue<bool>(out var b) => b,
                _ => false
            };
            var age = birth.Value.Year > 1900
                ? DateTime.Today.Year - birth.Value.Year - (DateTime.Today < birth.Value.AddYears(DateTime.Today.Year - birth.Value.Year) ? 1 : 0)
                : 0;
            snapshot.Birthdays.Add(new BirthdayItem
            {
                MilitaryId = record.Id,
                Day = birth.Value.Day.ToString("00"),
                Rank = record.Rank,
                WarName = record.WarName,
                Name = record.Name,
                Age = Math.Max(0, age),
                IsToday = birth.Value.Day == DateTime.Today.Day,
                Confirmed = confirmed
            });
        }

        var sorted = snapshot.Birthdays.OrderBy(x => int.TryParse(x.Day, out var d) ? d : 99)
            .ThenBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => Normalize(x.Name)).ToList();
        snapshot.Birthdays.Clear();
        foreach (var item in sorted) snapshot.Birthdays.Add(item);
    }

    private void LoadRankSummary(DashboardSnapshot snapshot, IReadOnlyList<MilitaryRecord> records)
    {
        foreach (var group in records.GroupBy(x => x.Rank)
                     .OrderBy(x => MilitaryRankService.GetOrder(x.Key))
                     .ThenBy(x => Normalize(x.Key)))
        {
            var item = new RankSummaryItem { Rank = group.Key, Count = group.Count() };
            foreach (var year in group.Select(x => x.FormationYear)
                         .Where(x => !string.IsNullOrWhiteSpace(x) && x != "—")
                         .GroupBy(x => x)
                         .OrderBy(x => int.TryParse(x.Key, out var value) ? value : 9999)
                         .ThenBy(x => x.Key))
                item.Years.Add(new FormationYearItem { Year = year.Key, Count = year.Count() });
            snapshot.RankSummary.Add(item);
        }
    }

    private async Task LoadBulletinsAsync(DashboardSnapshot snapshot)
    {
        var result = new List<(DateTime SortDate, BulletinItem Item)>();
        var biRoot = await _json.LoadNodeAsync(_paths.BulletinIndexFile);
        var biItems = biRoot switch
        {
            JsonArray array => array,
            JsonObject obj when obj["items"] is JsonArray array => array,
            _ => new JsonArray()
        };
        foreach (var obj in biItems.OfType<JsonObject>())
        {
            var path = Str(obj, "caminho_pdf", Str(obj, "path"));
            var date = ParseDate(Str(obj, "data_bi", Str(obj, "data", Str(obj, "data_iso"))));
            result.Add((date ?? DateTime.MinValue, new BulletinItem
            {
                Type = "BI",
                Number = Str(obj, "numero_bi", Str(obj, "numero", "—")),
                Date = date?.ToString("dd/MM/yyyy") ?? "—",
                File = Str(obj, "nome_arquivo", Str(obj, "nome_arquivo_original", string.IsNullOrWhiteSpace(path) ? "—" : Path.GetFileName(path))),
                Path = path
            }));
        }

        var furriel = await _json.LoadNodeAsync(_paths.FurrielIndexFile) as JsonObject;
        if (furriel?["files"] is JsonArray files)
        {
            foreach (var obj in files.OfType<JsonObject>())
            {
                var path = Str(obj, "stored_path", Str(obj, "signed_path"));
                var date = ParseDate(Str(obj, "data"));
                result.Add((date ?? DateTime.MinValue, new BulletinItem
                {
                    Type = "ADT",
                    Number = Str(obj, "boletim", "—"),
                    Date = date?.ToString("dd/MM/yyyy") ?? "—",
                    File = Str(obj, "original_name", Str(obj, "arquivo", string.IsNullOrWhiteSpace(path) ? "—" : Path.GetFileName(path))),
                    Path = path
                }));
            }
        }

        foreach (var item in result.OrderByDescending(x => x.SortDate).Take(80)) snapshot.Bulletins.Add(item.Item);
    }

    private async Task LoadFinancialAlertsAsync(DashboardSnapshot snapshot)
    {
        var today = DateTime.Today;
        var root = await _json.LoadNodeAsync(_paths.PaymentRemindersFile);
        var items = root switch
        {
            JsonArray array => array,
            JsonObject obj when obj["items"] is JsonArray array => array,
            _ => new JsonArray()
        };
        var index = 0;
        foreach (var obj in items.OfType<JsonObject>())
        {
            var itemStatus = Normalize(Str(obj, "status"));
            if (itemStatus is "concluido" or "ok" or "feito") continue;
            var itemDate = ParseDate(Str(obj, "prazo"));
            var itemDays = itemDate is null ? 999 : (itemDate.Value.Date - today).Days;
            var calculatedStatus = itemDays == 0 ? "HOJE" : itemDays is > 0 and <= 3 ? "ATENÇÃO" : "A FAZER";
            snapshot.FinancialAlerts.Add(new FinancialAlertItem
            {
                Id = Str(obj, "id", $"manual_{index++}"),
                Deadline = itemDate?.ToString("dd/MM/yyyy") ?? "—",
                Type = Str(obj, "categoria", Str(obj, "tipo", Str(obj, "titulo", "Lembrete de pagamento"))),
                Detail = Str(obj, "detalhe", Str(obj, "descricao")),
                Status = calculatedStatus,
                Manual = true
            });
        }

        var priority = new Dictionary<string, int> { ["HOJE"] = 0, ["ATENÇÃO"] = 1, ["A FAZER"] = 2 };
        var sorted = snapshot.FinancialAlerts.OrderBy(x => priority.GetValueOrDefault(x.Status, 9)).ThenBy(x => ParseDate(x.Deadline) ?? DateTime.MaxValue).Take(100).ToList();
        snapshot.FinancialAlerts.Clear();
        foreach (var item in sorted) snapshot.FinancialAlerts.Add(item);
    }


    private async Task<List<CalendarEventRecord>> LoadCalendarEventsAsync()
    {
        try
        {
            var root = await _json.LoadNodeAsync(_paths.CalendarEventsFile) as JsonObject;
            var items = root?["items"] as JsonArray;
            var list = new List<CalendarEventRecord>();
            if (items is null) return list;
            foreach (var node in items)
            {
                if (node is null) continue;
                try
                {
                    var item = node.Deserialize<CalendarEventRecord>();
                    if (item is not null && !string.IsNullOrWhiteSpace(item.Date)) list.Add(item);
                }
                catch { }
            }
            return list;
        }
        catch { return []; }
    }

    private void BuildReminderCalendar(
        DashboardSnapshot snapshot,
        IReadOnlyList<CalendarEventRecord> calendarEvents,
        RedDayStore redDayStore,
        DateTime displayedMonth,
        IReadOnlyList<MilitaryRecord> records,
        DutyRosterStore? dutyRoster)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(displayedMonth.Year, displayedMonth.Month, 1);
        snapshot.CalendarMonthTitle = monthStart.ToString("MMMM 'de' yyyy", CultureInfo.GetCultureInfo("pt-BR"));

        var reminderMap = new Dictionary<DateTime, List<string>>();
        var paymentMap = new Dictionary<DateTime, List<string>>();
        var serviceMap = new Dictionary<DateTime, List<string>>();
        var customEventMap = new Dictionary<DateTime, List<string>>();

        foreach (var reminder in snapshot.Reminders.Where(x => !x.Completed))
        {
            var date = ParseDate(reminder.Date);
            if (date is null) continue;
            var key = date.Value.Date;
            if (!reminderMap.TryGetValue(key, out var list))
            {
                list = [];
                reminderMap[key] = list;
            }
            var line = string.IsNullOrWhiteSpace(reminder.Title) ? "Lembrete" : reminder.Title.Trim();
            if (!string.IsNullOrWhiteSpace(reminder.Status) && reminder.Status != "Pendente") line += $" ({reminder.Status})";
            list.Add(line);
        }

        foreach (var alert in snapshot.FinancialAlerts)
        {
            var date = ParseDate(alert.Deadline);
            if (date is null) continue;
            var key = date.Value.Date;
            if (!paymentMap.TryGetValue(key, out var list))
            {
                list = [];
                paymentMap[key] = list;
            }
            var label = string.IsNullOrWhiteSpace(alert.Type) ? "Lembrete de pagamento" : alert.Type.Trim();
            if (!string.IsNullOrWhiteSpace(alert.Detail)) label += $": {alert.Detail.Trim()}";
            list.Add(label);
        }

        foreach (var item in calendarEvents)
        {
            if (!DateTime.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            var key = date.Date;
            var title = string.IsNullOrWhiteSpace(item.Title) ? item.Type : item.Title.Trim();
            var description = string.IsNullOrWhiteSpace(item.Description) ? string.Empty : $": {item.Description.Trim()}";
            var line = title + description;
            if (item.Type.Equals("Serviço", StringComparison.OrdinalIgnoreCase))
            {
                if (!serviceMap.TryGetValue(key, out var list))
                {
                    list = [];
                    serviceMap[key] = list;
                }
                list.Add(line);
            }
            else
            {
                if (!customEventMap.TryGetValue(key, out var list))
                {
                    list = [];
                    customEventMap[key] = list;
                }
                list.Add($"{item.Type}: {line}".Trim());
            }
        }

        var shift = ((int)monthStart.DayOfWeek + 6) % 7; // Monday first
        var gridStart = monthStart.AddDays(-shift);
        var dutyRosterCalendarFilter = DutyRosterCalendarFilterKey(dutyRoster);
        AddDutyRosterServices(serviceMap, dutyRoster, records, gridStart, gridStart.AddDays(41), dutyRosterCalendarFilter);

        snapshot.ReminderCalendarDays.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i).Date;
            reminderMap.TryGetValue(date, out var reminders);
            paymentMap.TryGetValue(date, out var payments);
            serviceMap.TryGetValue(date, out var services);
            customEventMap.TryGetValue(date, out var eventsOfDay);
            var redDay = RedDayService.GetInfo(date, redDayStore);
            var tooltipLines = new List<string> { date.ToString("dddd, dd 'de' MMMM 'de' yyyy", CultureInfo.GetCultureInfo("pt-BR")) };
            if (redDay.IsRedDay)
            {
                tooltipLines.Add(redDay.TooltipText);
            }
            if (reminders is { Count: > 0 })
            {
                tooltipLines.Add(string.Empty);
                tooltipLines.Add("Lembretes:");
                tooltipLines.AddRange(reminders.Select(x => "• " + x));
            }
            if (payments is { Count: > 0 })
            {
                tooltipLines.Add(string.Empty);
                tooltipLines.Add("Pagamento / corrida:");
                tooltipLines.AddRange(payments.Select(x => "• " + x));
            }
            if (services is { Count: > 0 })
            {
                tooltipLines.Add(string.Empty);
                tooltipLines.Add("Serviço:");
                tooltipLines.AddRange(services.Select(x => "• " + x));
            }
            if (eventsOfDay is { Count: > 0 })
            {
                tooltipLines.Add(string.Empty);
                tooltipLines.Add("Outros eventos:");
                tooltipLines.AddRange(eventsOfDay.Select(x => "• " + x));
            }
            if ((reminders?.Count ?? 0) == 0 && (payments?.Count ?? 0) == 0 && (services?.Count ?? 0) == 0 && (eventsOfDay?.Count ?? 0) == 0)
            {
                tooltipLines.Add(string.Empty);
                tooltipLines.Add("Clique para adicionar evento, serviço ou observação.");
            }
            else
            {
                tooltipLines.Add(string.Empty);
                tooltipLines.Add("Clique para editar os eventos deste dia.");
            }

            snapshot.ReminderCalendarDays.Add(new CalendarDayItem
            {
                Date = date,
                DayNumber = date.Day.ToString(CultureInfo.InvariantCulture),
                IsCurrentMonth = date.Month == monthStart.Month,
                IsToday = date == today,
                IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                IsRedDay = redDay.IsRedDay,
                RedDayReason = redDay.TooltipText,
                HasReminder = (reminders?.Count ?? 0) > 0,
                HasPaymentReminder = (payments?.Count ?? 0) > 0,
                HasService = (services?.Count ?? 0) > 0,
                HasCustomEvent = (eventsOfDay?.Count ?? 0) > 0,
                ReminderCount = reminders?.Count ?? 0,
                PaymentReminderCount = payments?.Count ?? 0,
                ServiceCount = services?.Count ?? 0,
                CustomEventCount = eventsOfDay?.Count ?? 0,
                Tooltip = string.Join(Environment.NewLine, tooltipLines)
            });
        }

        var monthCustom = calendarEvents.Count(x => DateTime.TryParseExact(x.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Month == monthStart.Month && d.Year == monthStart.Year);
        var monthRosterServices = CountDutyRosterServices(dutyRoster, monthStart, dutyRosterCalendarFilter);
        snapshot.CalendarSummary = $"{snapshot.Reminders.Count(x => !x.Completed && ParseDate(x.Date) is { } d && d.Month == monthStart.Month && d.Year == monthStart.Year)} lembrete(s), {snapshot.FinancialAlerts.Count(x => ParseDate(x.Deadline) is { } d && d.Month == monthStart.Month && d.Year == monthStart.Year)} evento(s) de pagamento, {monthRosterServices} serviço(s) da escala e {monthCustom} registro(s) manuais neste mês.";
    }

    private static void AddDutyRosterServices(
        Dictionary<DateTime, List<string>> serviceMap,
        DutyRosterStore? dutyRoster,
        IReadOnlyList<MilitaryRecord> records,
        DateTime from,
        DateTime until,
        string calendarPersonKey)
    {
        if (dutyRoster?.Months is null || dutyRoster.Months.Count == 0) return;
        var byId = records.Where(x => x.Id > 0).ToDictionary(x => x.Id);
        var extras = dutyRoster.ExtraPeople?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToDictionary(x => "E:" + x.Trim().ToUpperInvariant(), x => x.Trim(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var month in dutyRoster.Months.Values)
        foreach (var assignment in month.Assignments)
        {
            if (!string.IsNullOrWhiteSpace(calendarPersonKey) &&
                !assignment.Value.Equals(calendarPersonKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateTime.TryParseExact(assignment.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            date = date.Date;
            if (date < from.Date || date > until.Date) continue;
            if (!serviceMap.TryGetValue(date, out var list))
            {
                list = [];
                serviceMap[date] = list;
            }

            var label = DutyRosterAssignmentLabel(assignment.Value, byId, extras);
            if (!list.Any(x => x.Equals(label, StringComparison.OrdinalIgnoreCase)))
                list.Add(label);
        }
    }

    private static int CountDutyRosterServices(DutyRosterStore? dutyRoster, DateTime monthStart, string calendarPersonKey)
    {
        if (dutyRoster?.Months is null) return 0;
        var key = DutyRosterService.MonthKey(monthStart.Year, monthStart.Month);
        return dutyRoster.Months.TryGetValue(key, out var month)
            ? month.Assignments.Count(x =>
                (string.IsNullOrWhiteSpace(calendarPersonKey) || x.Value.Equals(calendarPersonKey, StringComparison.OrdinalIgnoreCase)) &&
                DateTime.TryParseExact(x.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                date.Year == monthStart.Year &&
                date.Month == monthStart.Month)
            : 0;
    }

    private static string DutyRosterCalendarFilterKey(DutyRosterStore? dutyRoster)
        => dutyRoster?.OperationalCalendarPersonKey?.Trim() ?? string.Empty;

    private static string DutyRosterAssignmentLabel(
        string personKey,
        IReadOnlyDictionary<int, MilitaryRecord> records,
        IReadOnlyDictionary<string, string> extras)
    {
        if (TryGetDutyRosterMilitaryId(personKey, out var id) && records.TryGetValue(id, out var military))
        {
            var rank = MilitaryRankService.ShortName(military.Rank);
            var name = string.IsNullOrWhiteSpace(military.WarName) || military.WarName == "—"
                ? CleanName(military.Name, military.Rank)
                : military.WarName.Trim();
            return $"Sgt Dia: {rank} {name}".Trim();
        }

        if (extras.TryGetValue(personKey, out var extra))
            return "Sgt Dia: " + extra;

        return "Sgt Dia: " + personKey;
    }

    private static bool TryGetDutyRosterMilitaryId(string personKey, out int id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(personKey)) return false;
        var value = personKey.Trim();
        if (value.StartsWith("M:", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
    }


    private void BuildOperationalIssues(DashboardSnapshot snapshot, IReadOnlyList<MilitaryRecord> records)
    {
        var issues = new List<OperationalIssueItem>();
        var today = DateTime.Today;

        if (!snapshot.DatabaseStatus.Contains("militar", StringComparison.OrdinalIgnoreCase) || snapshot.ActiveMilitaryCount == 0)
        {
            AddIssue(issues, "Crítica", "Sistema", "Banco oficial sem efetivo carregado", snapshot.DatabaseStatus, "Diagnóstico do banco", "Verificar banco de dados antes de gerar notas.");
        }

        var lastBackup = ParseDate(snapshot.LastBackup);
        if (snapshot.BackupCount == 0 || lastBackup is null)
        {
            AddIssue(issues, "Crítica", "Sistema", "Nenhum backup localizado", "Não foi encontrado backup ZIP na pasta oficial.", "Backup", "Criar backup seguro agora.");
        }
        else if ((today - lastBackup.Value.Date).TotalDays >= 7)
        {
            AddIssue(issues, "Atenção", "Sistema", "Backup antigo", $"Último backup em {snapshot.LastBackup}.", "Backup", "Criar backup antes de importar boletins ou recalcular pagamentos.");
        }

        foreach (var reminder in snapshot.Reminders.Where(x => !x.Completed && x.Status is "Atrasado" or "Hoje").Take(12))
        {
            var severity = reminder.Status == "Atrasado" ? "Crítica" : "Hoje";
            AddIssue(issues, severity, "Rotina", reminder.Title, $"Prazo: {reminder.Date} • prioridade {reminder.Priority}", "Lembretes", "Abrir lembrete e concluir ou remarcar.");
        }

        foreach (var alert in snapshot.FinancialAlerts.Where(x => x.Status is "HOJE" or "ATENÇÃO").Take(14))
        {
            var severity = alert.Status == "HOJE" ? "Hoje" : "Atenção";
            var title = string.IsNullOrWhiteSpace(alert.Type) ? "Pendência de pagamento" : alert.Type;
            AddIssue(issues, severity, "Pagamento", title, $"Prazo: {alert.Deadline} • {alert.Detail}", "Corrida / pagamento", "Conferir antes da corrida e marcar como resolvido.");
        }

        foreach (var record in records
                     .Select(x => new { Record = x, Value = ParseMoney(x.TransportValueRaw) })
                     .Where(x => ReceivesTransport(x.Record) && x.Value >= 700)
                     .OrderByDescending(x => x.Value)
                     .Take(12))
        {
            issues.Add(CreateIssue(
                record.Value >= 1200 ? "Crítica" : "Atenção",
                "Pagamento",
                "SAT acima de R$ 700",
                $"SAT mensal: {record.Value.ToString("C", CultureInfo.GetCultureInfo("pt-BR"))}",
                "SAT / Cadastro AT",
                record.Value >= 1200 ? "Conferir autorização/justificativa e rota." : "Conferir endereço, linha, valor e DA do mês.",
                record.Record));
        }

        foreach (var issue in BuildVacationPaymentIssues(snapshot, records).Take(80))
            issues.Add(issue);

        var missingSatIssues = records
            .Where(x => !ReceivesTransport(x))
            .OrderBy(RankSort)
            .ThenBy(x => Normalize(x.Name))
            .Select(record => CreateIssue(
                "Normal",
                "Cadastro",
                "Militar sem SAT",
                "Sem valor ou marcado como não recebe.",
                "Cadastro SAT",
                "Confirmar se realmente não recebe ou se falta implantação.",
                record))
            .ToList();
        foreach (var issue in missingSatIssues.Take(250)) snapshot.RegisterMissingTransportAidIssues.Add(issue);
        issues.AddRange(missingSatIssues.Take(40));

        var missingAddressIssues = records
            .Where(x => IsBlankData(x.Address))
            .OrderByDescending(ReceivesTransport)
            .ThenBy(RankSort)
            .ThenBy(x => Normalize(x.Name))
            .Select(record => CreateIssue(
                ReceivesTransport(record) ? "Atenção" : "Normal",
                "Cadastro",
                ReceivesTransport(record) ? "Endereço ausente para quem recebe SAT" : "Endereço ausente",
                ReceivesTransport(record) ? "Recebe SAT e está sem endereço." : "Sem endereço no cadastro.",
                ReceivesTransport(record) ? "Cadastro SAT" : "Cadastro",
                ReceivesTransport(record) ? "Completar endereço antes de recalcular o SAT." : "Completar endereço para contato, PHPM e conferências.",
                record))
            .ToList();
        foreach (var issue in missingAddressIssues.Take(250)) snapshot.RegisterMissingAddressIssues.Add(issue);
        issues.AddRange(missingAddressIssues.Take(40));

        var missingCpfIssues = records
            .Where(x => IsBlankData(x.Cpf))
            .OrderBy(RankSort)
            .ThenBy(x => Normalize(x.Name))
            .Select(record => CreateIssue(
                "Atenção",
                "Cadastro",
                "CPF ausente no cadastro",
                "CPF não informado.",
                "Cadastro",
                "Completar CPF para evitar erro em notas e pesquisas.",
                record))
            .ToList();
        foreach (var issue in missingCpfIssues.Take(250)) snapshot.RegisterMissingCpfIssues.Add(issue);
        issues.AddRange(missingCpfIssues.Take(40));

        var bankIssues = records
            .Where(x => IsBlankData(x.Bank) || IsBlankData(x.Agency) || IsBlankData(x.Account))
            .OrderBy(RankSort)
            .ThenBy(x => Normalize(x.Name))
            .Select(record => CreateIssue(
                "Normal",
                "Cadastro",
                "Dados bancários incompletos",
                $"Banco {BlankData(record.Bank)} • Ag {BlankData(record.Agency)} • CC {BlankData(record.Account)}",
                "Cadastro bancário",
                "Completar banco, agência e conta antes de conferência de pagamento.",
                record))
            .ToList();
        foreach (var issue in bankIssues.Take(250)) snapshot.RegisterBankIssues.Add(issue);
        issues.AddRange(bankIssues.Take(30));

        var missingPhotoIssues = records
            .Where(x => IsBlankData(x.PhotoPath) || !PhotoExists(x.PhotoPath))
            .OrderBy(RankSort)
            .ThenBy(x => Normalize(x.Name))
            .Select(record => CreateIssue(
                "Normal",
                "Cadastro",
                "Foto ausente",
                IsBlankData(record.PhotoPath) ? "Sem foto vinculada." : "Arquivo de foto não encontrado.",
                "Cadastro",
                "Cadastrar ou corrigir a foto para carteira, relações e identificação visual.",
                record))
            .ToList();
        foreach (var issue in missingPhotoIssues.Take(250)) snapshot.RegisterMissingPhotoIssues.Add(issue);
        issues.AddRange(missingPhotoIssues.Take(25));

        var identityIssues = records
            .Where(x => IsBlankData(x.PrecCp) || IsBlankData(x.MilitaryId) || ParseDate(x.BirthDate) is null)
            .OrderBy(RankSort)
            .ThenBy(x => Normalize(x.Name))
            .Select(record => CreateIssue(
                "Atenção",
                "Cadastro",
                "Identificação incompleta",
                $"PREC-CP {BlankData(record.PrecCp)} • IDT {BlankData(record.MilitaryId)} • nasc. {BlankData(record.BirthDate)}",
                "Cadastro",
                "Completar identificação para documentos, pesquisas e conferências.",
                record))
            .ToList();
        foreach (var issue in identityIssues.Take(250)) snapshot.RegisterIdentityIssues.Add(issue);
        issues.AddRange(identityIssues.Take(30));

        var recentBulletinDate = snapshot.Bulletins.Select(x => ParseDate(x.Date)).Where(x => x is not null).Select(x => x!.Value.Date).DefaultIfEmpty(DateTime.MinValue).Max();
        if (recentBulletinDate == DateTime.MinValue)
        {
            AddIssue(issues, "Atenção", "Boletim", "Nenhum BI/ADT importado no painel", "O dashboard não encontrou boletins indexados.", "Boletins", "Importar BI/ADT salvos na pasta oficial.");
        }
        else if ((today - recentBulletinDate).TotalDays >= 15)
        {
            AddIssue(issues, "Atenção", "Boletim", "Boletins podem estar desatualizados", $"Último registro localizado em {recentBulletinDate:dd/MM/yyyy}.", "Boletins", "Importar boletins/aditamentos mais recentes.");
        }

        var adtCount = snapshot.Bulletins.Count(x => x.Type.Equals("ADT", StringComparison.OrdinalIgnoreCase));
        if (adtCount == 0)
        {
            AddIssue(issues, "Atenção", "Boletim", "Aditamento do Furriel não localizado", "Nenhum ADT apareceu entre os boletins recentes.", "ADT Furriel", "Importar aditamentos para cruzar pagamentos.");
        }

        foreach (var birthday in snapshot.Birthdays.Where(x => x.IsToday).Take(8))
        {
            AddIssue(issues, "Hoje", "Rotina", "Aniversariante hoje", $"{birthday.Rank} {birthday.WarName} • {birthday.Age} ano(s)", "Aniversariantes", "Conferir PHPM e, se for o caso, gerar nota.");
        }

        var unconfirmedBirthdays = snapshot.Birthdays.Count(x => !x.Confirmed);
        if (unconfirmedBirthdays > 0)
        {
            AddIssue(issues, "Normal", "Rotina", "Conferência de aniversariantes pendente", $"{unconfirmedBirthdays} militar(es) do mês sem confirmação.", "Aniversariantes", "Abrir conferência mensal.");
        }

        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Crítica"] = 0,
            ["Hoje"] = 1,
            ["Atenção"] = 2,
            ["Normal"] = 3
        };

        foreach (var issue in issues
                     .OrderBy(x => order.GetValueOrDefault(x.Severity, 9))
                     .ThenBy(x => x.Category)
                     .ThenBy(x => x.SortKey)
                     .ThenBy(x => x.Title)
                     .Take(80))
        {
            snapshot.OperationalIssues.Add(issue);
            switch (issue.Category)
            {
                case "Pagamento":
                    snapshot.PaymentIssues.Add(issue);
                    if (!issue.Source.Equals("Férias", StringComparison.OrdinalIgnoreCase))
                        snapshot.PaymentGeneralIssues.Add(issue);
                    break;
                case "Boletim": snapshot.BulletinIssues.Add(issue); break;
                case "Cadastro": snapshot.RegisterIssues.Add(issue); break;
                default: snapshot.RoutineIssues.Add(issue); break;
            }
        }

        snapshot.OperationalIssueTotal = snapshot.OperationalIssues.Count;
        snapshot.OperationalIssueCritical = snapshot.OperationalIssues.Count(x => x.Severity == "Crítica");
        snapshot.OperationalIssueAttention = snapshot.OperationalIssues.Count(x => x.Severity is "Atenção" or "Hoje");
        snapshot.OperationalIssueSummary = snapshot.OperationalIssueTotal == 0
            ? "Nenhuma pendência operacional encontrada no painel."
            : $"{snapshot.OperationalIssueTotal} pendência(s): {snapshot.OperationalIssueCritical} crítica(s), {snapshot.OperationalIssueAttention} para atenção imediata.";
    }

    private static void AddIssue(List<OperationalIssueItem> issues, string severity, string category, string title, string detail, string source, string action)
        => issues.Add(CreateIssue(severity, category, title, detail, source, action));

    private static OperationalIssueItem CreateIssue(string severity, string category, string title, string detail, string source, string action, MilitaryRecord? person = null)
    {
        var item = new OperationalIssueItem
        {
            Severity = severity,
            Category = category,
            Title = string.IsNullOrWhiteSpace(title) ? "Pendência operacional" : title.Trim(),
            Detail = string.IsNullOrWhiteSpace(detail) ? "—" : detail.Trim(),
            Source = string.IsNullOrWhiteSpace(source) ? category : source.Trim(),
            Action = string.IsNullOrWhiteSpace(action) ? "Conferir." : action.Trim(),
            SortKey = Normalize($"{severity} {category} {title} {detail}")
        };
        if (person is not null)
        {
            item.PersonRank = person.Rank;
            item.PersonName = CleanName(person.Name, person.Rank);
            item.PersonWarName = IsBlankData(person.WarName) || person.WarName == "—" ? string.Empty : person.WarName.Trim();
        }
        return item;
    }

    private static bool IsBlankData(string? value)
    {
        var text = Normalize(value);
        return string.IsNullOrWhiteSpace(text) || text is "-" or "—" or "nao informado" or "sem" or "s/n" or "sn";
    }

    private static string BlankData(string? value)
        => IsBlankData(value) ? "—" : value!.Trim();

    private IReadOnlyList<OperationalIssueItem> BuildVacationPaymentIssues(DashboardSnapshot snapshot, IReadOnlyList<MilitaryRecord> records)
    {
        var result = new List<OperationalIssueItem>();
        if (string.IsNullOrWhiteSpace(snapshot.DatabasePath) || !File.Exists(snapshot.DatabasePath)) return result;

        try
        {
            var targetMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
            var monthStart = targetMonth;
            var monthEnd = targetMonth.AddMonths(1).AddDays(-1);
            var byId = records.Where(x => x.Id > 0).ToDictionary(x => x.Id);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = snapshot.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 8
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            if (!TableExists(connection, "ferias_periodos_wpf") || !TableExists(connection, "ferias_alocacoes_wpf")) return result;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT a.id,a.ano,a.periodo_id,a.militar_id,a.dias,a.pago,a.pago_em,
                       a.aux_alimentacao_pago,a.aux_alimentacao_pago_em,
                       p.indice,p.nome,p.data_inicio,p.data_fim
                  FROM ferias_alocacoes_wpf a
                  JOIN ferias_periodos_wpf p ON p.id=a.periodo_id
                 WHERE a.ano=$ano
                   AND (
                        (p.data_inicio IS NOT NULL AND p.data_fim IS NOT NULL AND date(p.data_inicio) <= date($fim) AND date(p.data_fim) >= date($inicio))
                        OR ((p.data_inicio IS NULL OR p.data_fim IS NULL) AND p.indice=$mes)
                   );
                """;
            cmd.Parameters.AddWithValue("$ano", targetMonth.Year);
            cmd.Parameters.AddWithValue("$inicio", monthStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$fim", monthEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$mes", targetMonth.Month);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var militaryId = reader.GetInt32(3);
                if (!byId.TryGetValue(militaryId, out var record)) continue;

                var days = reader.GetInt32(4);
                var paid = reader.GetInt32(5) != 0;
                var paidAt = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                var foodAidPaid = !reader.IsDBNull(7) && reader.GetInt32(7) != 0;
                var periodName = reader.IsDBNull(10) ? $"{reader.GetInt32(9)}º Período" : reader.GetString(10);
                var start = ParseDate(reader.IsDBNull(11) ? null : reader.GetString(11));
                var end = ParseDate(reader.IsDBNull(12) ? null : reader.GetString(12));
                var foodAidStatus = RequiresVacationFoodAid(record)
                    ? foodAidPaid ? " • auxílio-alimentação pago" : " • auxílio-alimentação pendente"
                    : string.Empty;
                var periodText = start is null && end is null
                    ? $"{periodName} • referência {targetMonth:MM/yyyy}"
                    : $"{periodName} • {start:dd/MM/yyyy} a {end:dd/MM/yyyy}";
                var detail = paid
                    ? $"{periodText} • {days} dia(s) • pago{FormatPaidAt(paidAt)}{foodAidStatus}"
                    : $"{periodText} • {days} dia(s) • falta pagar{foodAidStatus}";

                var issue = CreateIssue(
                    paid ? "Normal" : "Atenção",
                    "Pagamento",
                    paid ? "Férias pagas" : "Férias pendentes",
                    detail,
                    "Férias",
                    paid ? "Conferido. Manter registro de pagamento." : "Solicitar/confirmar pagamento antes do militar entrar de férias.",
                    record);
                result.Add(issue);
            }

            foreach (var issue in result
                         .OrderBy(x => x.Severity == "Atenção" ? 0 : 1)
                         .ThenBy(x => MilitaryRankService.GetOrder(x.PersonRank))
                         .ThenBy(x => Normalize(x.PersonName))
                         .Take(250))
                snapshot.PaymentVacationIssues.Add(issue);
        }
        catch { }

        return result;
    }

    private static string FormatPaidAt(string value)
        => ParseDate(value) is { } date ? $" em {date:dd/MM/yyyy}" : string.Empty;

    private static bool RequiresVacationFoodAid(MilitaryRecord record)
    {
        var rank = Normalize(MilitaryRankService.Canonicalize(record.Rank));
        return rank.Contains("cabo", StringComparison.OrdinalIgnoreCase) || rank.Contains("soldado", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PhotoExists(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { return File.Exists(value); }
        catch { return false; }
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture) > 0;
    }

    private void LoadBackupInformation(DashboardSnapshot snapshot)
    {
        try
        {
            if (!Directory.Exists(_paths.BackupsDirectory)) return;
            var files = Directory.EnumerateFiles(_paths.BackupsDirectory, "*.zip")
                .OrderByDescending(File.GetLastWriteTime)
                .ToList();
            snapshot.BackupCount = files.Count;
            snapshot.LastBackup = files.Count == 0 ? "—" : File.GetLastWriteTime(files[0]).ToString("dd/MM/yyyy HH:mm");
        }
        catch { }
    }

    private static bool ReceivesTransport(MilitaryRecord record)
    {
        var normalized = Normalize(record.ReceivesTransportRaw);
        if (normalized.StartsWith("sim") || normalized is "s" or "true" or "1" or "recebe" or "ativo") return true;
        if (normalized.StartsWith("nao") || normalized is "n" or "false" or "0" or "sem" or "inativo") return false;
        return ParseMoney(record.TransportValueRaw) > 0;
    }

    private static MilitaryItem ToMilitaryItem(MilitaryRecord record) => new()
    {
        Id = record.Id,
        Rank = record.Rank,
        Name = record.Name,
        WarName = record.WarName,
        Cpf = record.Cpf,
        FormationYear = record.FormationYear
    };

    private static int RankSort(MilitaryRecord record)
        => MilitaryRankService.GetOrder(record.Rank);

    private static string CanonRank(string value)
    {
        var shortName = MilitaryRankService.ShortName(value);
        return string.IsNullOrWhiteSpace(shortName) || shortName == "—" ? "Outros" : shortName;
    }

    private static string CleanName(string? name, string? rank)
    {
        var text = (name ?? string.Empty).Trim();
        var shortRank = MilitaryRankService.ShortName(rank ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(shortRank) &&
            text.StartsWith(shortRank + " ", StringComparison.CurrentCultureIgnoreCase))
            text = text[shortRank.Length..].Trim();
        return string.IsNullOrWhiteSpace(text) ? "Militar" : text;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var form = value.Normalize(NormalizationForm.FormD);
        return new string(form.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant().Trim();
    }

    private static double ParseMoney(string? value)
    {
        var text = (value ?? string.Empty).Replace("R$", string.Empty).Replace(" ", string.Empty);
        if (text.Contains(',')) text = text.Replace(".", string.Empty).Replace(',', '.');
        text = new string(text.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static DateTime? ParseDate(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] formats = ["yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "dd/MM/yy", "dd/MM"];
        foreach (var format in formats)
            if (DateTime.TryParseExact(text.Length > 10 ? text[..10] : text, format, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var result))
                return result;
        return DateTime.TryParse(text, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var fallback) ? fallback : null;
    }

    private static DateTime ThirdBusinessDay(int year, int month)
    {
        var count = 0;
        for (var day = 1; day <= DateTime.DaysInMonth(year, month); day++)
        {
            var current = new DateTime(year, month, day);
            if (current.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (++count == 3) return current;
        }
        return new DateTime(year, month, 3);
    }

    private static string Str(JsonObject? obj, string key, string fallback = "")
    {
        if (obj?[key] is null) return fallback;
        try { return obj[key]!.GetValue<string>(); }
        catch { return obj[key]!.ToString(); }
    }

    private static bool Bool(JsonObject? obj, string key)
    {
        try { return obj?[key]?.GetValue<bool>() ?? false; }
        catch { return bool.TryParse(obj?[key]?.ToString(), out var value) && value; }
    }

    private static int JsonInt(JsonObject? obj, string key)
    {
        try { return obj?[key]?.GetValue<int>() ?? 0; }
        catch { return int.TryParse(obj?[key]?.ToString(), out var value) ? value : 0; }
    }

    private static string Str(SqliteDataReader reader, IReadOnlyDictionary<string, int> columns, params string[] names)
        => Str(reader, columns, names, string.Empty);

    private static string Str(SqliteDataReader reader, IReadOnlyDictionary<string, int> columns, string name1, string name2, string fallback)
        => Str(reader, columns, [name1, name2], fallback);

    private static string Str(SqliteDataReader reader, IReadOnlyDictionary<string, int> columns, string name1, string name2, string name3, string fallback)
        => Str(reader, columns, [name1, name2, name3], fallback);

    private static string Str(SqliteDataReader reader, IReadOnlyDictionary<string, int> columns, string[] names, string fallback)
    {
        foreach (var name in names)
        {
            if (!columns.TryGetValue(name, out var index) || reader.IsDBNull(index)) continue;
            var value = Convert.ToString(reader.GetValue(index));
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return fallback;
    }

    private static int Int(SqliteDataReader reader, IReadOnlyDictionary<string, int> columns, params string[] names)
    {
        var value = Str(reader, columns, names, "0");
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private sealed class MilitaryRecord
    {
        public int Id { get; init; }
        public string Rank { get; init; } = "Outros";
        public string Name { get; init; } = "—";
        public string WarName { get; init; } = "—";
        public string Cpf { get; init; } = string.Empty;
        public string PrecCp { get; init; } = string.Empty;
        public string MilitaryId { get; init; } = string.Empty;
        public string Bank { get; init; } = string.Empty;
        public string Agency { get; init; } = string.Empty;
        public string Account { get; init; } = string.Empty;
        public string PhotoPath { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string FormationYear { get; init; } = "—";
        public string BirthDate { get; init; } = string.Empty;
        public string ReceivesTransportRaw { get; init; } = string.Empty;
        public string TransportValueRaw { get; init; } = string.Empty;
    }
}
