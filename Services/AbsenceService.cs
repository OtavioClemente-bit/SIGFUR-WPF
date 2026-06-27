using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class AbsenceService
{
    private readonly AppPaths _paths;
    public AbsenceService(AppPaths paths) { _paths = paths; }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.AbsenceDatabaseFile)!);
        var connection = new SqliteConnection($"Data Source={_paths.AbsenceDatabaseFile}");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public Task InitializeAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS absence_occurrences (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                military_id INTEGER NOT NULL,
                rank TEXT NOT NULL DEFAULT '',
                name TEXT NOT NULL DEFAULT '',
                war_name TEXT NOT NULL DEFAULT '',
                occurrence_date TEXT NOT NULL,
                occurrence_time TEXT NOT NULL DEFAULT '',
                type TEXT NOT NULL,
                minutes INTEGER NOT NULL DEFAULT 0,
                justified INTEGER NOT NULL DEFAULT 0,
                reason TEXT NOT NULL DEFAULT '',
                measure TEXT NOT NULL DEFAULT '',
                notes TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_absence_date ON absence_occurrences(occurrence_date);
            CREATE INDEX IF NOT EXISTS idx_absence_military ON absence_occurrences(military_id);
            CREATE TABLE IF NOT EXISTS absence_closures (
                year INTEGER NOT NULL,
                month INTEGER NOT NULL,
                closed_at TEXT NOT NULL,
                total INTEGER NOT NULL DEFAULT 0,
                absences INTEGER NOT NULL DEFAULT 0,
                delays INTEGER NOT NULL DEFAULT 0,
                unjustified INTEGER NOT NULL DEFAULT 0,
                delay_minutes INTEGER NOT NULL DEFAULT 0,
                note TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(year, month)
            );
            """;
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public async Task<List<AbsenceOccurrence>> ListAsync(int year, int month, int? militaryId = null, string type = "Todos", string query = "")
    {
        await InitializeAsync();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,military_id,rank,name,war_name,occurrence_date,occurrence_time,type,minutes,justified,reason,measure,notes,created_at
              FROM absence_occurrences
             WHERE substr(occurrence_date,1,4)=$year AND substr(occurrence_date,6,2)=$month
               AND ($militaryId IS NULL OR military_id=$militaryId)
               AND ($type='Todos' OR type=$type)
             ORDER BY occurrence_date DESC, occurrence_time DESC, id DESC;
            """;
        command.Parameters.AddWithValue("$year", year.ToString("0000"));
        command.Parameters.AddWithValue("$month", month.ToString("00"));
        command.Parameters.AddWithValue("$militaryId", militaryId is null ? DBNull.Value : militaryId.Value);
        command.Parameters.AddWithValue("$type", type);
        var list = new List<AbsenceOccurrence>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = Read(reader);
            if (!string.IsNullOrWhiteSpace(query))
            {
                var hay = MilitaryRankService.Normalize($"{row.Rank} {row.Name} {row.WarName} {row.Type} {row.Reason} {row.Measure} {row.Notes}");
                var terms = MilitaryRankService.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!terms.All(hay.Contains)) continue;
            }
            list.Add(row);
        }
        return list;
    }

    public async Task<long> SaveAsync(AbsenceOccurrence item)
    {
        await InitializeAsync();
        using var connection = Open();
        using var command = connection.CreateCommand();
        if (item.Id == 0)
        {
            command.CommandText = """
                INSERT INTO absence_occurrences(military_id,rank,name,war_name,occurrence_date,occurrence_time,type,minutes,justified,reason,measure,notes,created_at)
                VALUES($military,$rank,$name,$war,$date,$time,$type,$minutes,$justified,$reason,$measure,$notes,$created);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE absence_occurrences SET military_id=$military,rank=$rank,name=$name,war_name=$war,occurrence_date=$date,occurrence_time=$time,
                    type=$type,minutes=$minutes,justified=$justified,reason=$reason,measure=$measure,notes=$notes WHERE id=$id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", item.Id);
        }
        command.Parameters.AddWithValue("$military", item.MilitaryId);
        command.Parameters.AddWithValue("$rank", item.Rank ?? string.Empty);
        command.Parameters.AddWithValue("$name", item.Name ?? string.Empty);
        command.Parameters.AddWithValue("$war", item.WarName ?? string.Empty);
        command.Parameters.AddWithValue("$date", item.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$time", item.Time ?? string.Empty);
        command.Parameters.AddWithValue("$type", item.Type ?? "ATRASO");
        command.Parameters.AddWithValue("$minutes", Math.Max(0, item.Minutes));
        command.Parameters.AddWithValue("$justified", item.Justified ? 1 : 0);
        command.Parameters.AddWithValue("$reason", item.Reason ?? string.Empty);
        command.Parameters.AddWithValue("$measure", item.Measure ?? string.Empty);
        command.Parameters.AddWithValue("$notes", item.Notes ?? string.Empty);
        command.Parameters.AddWithValue("$created", (item.CreatedAt == default ? DateTime.Now : item.CreatedAt).ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task DeleteAsync(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM absence_occurrences WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task SaveClosureAsync(int year, int month, AbsenceSummary summary, string note)
    {
        await InitializeAsync();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO absence_closures(year,month,closed_at,total,absences,delays,unjustified,delay_minutes,note)
            VALUES($year,$month,$closed,$total,$absences,$delays,$unjustified,$minutes,$note)
            ON CONFLICT(year,month) DO UPDATE SET
                closed_at=excluded.closed_at,total=excluded.total,absences=excluded.absences,delays=excluded.delays,
                unjustified=excluded.unjustified,delay_minutes=excluded.delay_minutes,note=excluded.note;
            """;
        command.Parameters.AddWithValue("$year", year);
        command.Parameters.AddWithValue("$month", month);
        command.Parameters.AddWithValue("$closed", DateTime.Now.ToString("O"));
        command.Parameters.AddWithValue("$total", summary.Total);
        command.Parameters.AddWithValue("$absences", summary.Absences);
        command.Parameters.AddWithValue("$delays", summary.Delays);
        command.Parameters.AddWithValue("$unjustified", summary.Unjustified);
        command.Parameters.AddWithValue("$minutes", summary.DelayMinutes);
        command.Parameters.AddWithValue("$note", note ?? string.Empty);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<AbsenceClosure?> GetClosureAsync(int year, int month)
    {
        await InitializeAsync();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT closed_at,total,absences,delays,unjustified,delay_minutes,note FROM absence_closures WHERE year=$year AND month=$month";
        command.Parameters.AddWithValue("$year", year);
        command.Parameters.AddWithValue("$month", month);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        DateTime.TryParse(reader.GetString(0), out var closedAt);
        return new AbsenceClosure
        {
            Year = year,
            Month = month,
            ClosedAt = closedAt == default ? DateTime.Now : closedAt,
            Summary = new AbsenceSummary
            {
                Total = reader.GetInt32(1),
                Absences = reader.GetInt32(2),
                Delays = reader.GetInt32(3),
                Unjustified = reader.GetInt32(4),
                DelayMinutes = reader.GetInt32(5)
            },
            Note = reader.GetString(6)
        };
    }

    public static AbsenceSummary Summarize(IEnumerable<AbsenceOccurrence> rows)
    {
        var list = rows.ToList();
        return new AbsenceSummary
        {
            Total = list.Count,
            Absences = list.Count(x => x.Type.Equals("FALTA", StringComparison.OrdinalIgnoreCase)),
            Delays = list.Count(x => x.Type.Equals("ATRASO", StringComparison.OrdinalIgnoreCase)),
            Unjustified = list.Count(x => !x.Justified),
            DelayMinutes = list.Where(x => x.Type.Equals("ATRASO", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Minutes)
        };
    }

    private static AbsenceOccurrence Read(SqliteDataReader reader)
    {
        DateTime.TryParse(reader.GetString(5), out var date);
        DateTime.TryParse(reader.GetString(13), out var created);
        return new AbsenceOccurrence
        {
            Id = reader.GetInt64(0), MilitaryId = reader.GetInt32(1), Rank = reader.GetString(2), Name = reader.GetString(3), WarName = reader.GetString(4),
            Date = date == default ? DateTime.Today : date, Time = reader.GetString(6), Type = reader.GetString(7), Minutes = reader.GetInt32(8), Justified = reader.GetInt32(9) != 0,
            Reason = reader.GetString(10), Measure = reader.GetString(11), Notes = reader.GetString(12), CreatedAt = created == default ? DateTime.Now : created
        };
    }
}
