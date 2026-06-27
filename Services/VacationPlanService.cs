using System.Security;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class VacationPlanService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly MilitaryRepository _military;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _schemaReady;
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public VacationPlanService(AppPaths paths, JsonFileService json, MilitaryRepository military, LogService log)
    {
        _paths = paths;
        _json = json;
        _military = military;
        _log = log;
    }

    public string OutputDirectory => _paths.VacationOutputDirectory;

    private SqliteConnection OpenConnection()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 12
        };
        var connection = new SqliteConnection(cs.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=12000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            await _military.EnsureSchemaAsync(cancellationToken);
            await using var connection = OpenConnection();
            await using (var databaseMode = connection.CreateCommand())
            {
                databaseMode.CommandText = "PRAGMA journal_mode=WAL;";
                await databaseMode.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS ferias_periodos_wpf(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ano INTEGER NOT NULL,
                    indice INTEGER NOT NULL,
                    nome TEXT NOT NULL DEFAULT '',
                    data_inicio TEXT,
                    data_fim TEXT,
                    especial INTEGER NOT NULL DEFAULT 0,
                    criado_em TEXT,
                    atualizado_em TEXT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_ferias_periodos_wpf_padrao
                    ON ferias_periodos_wpf(ano, indice) WHERE especial=0;
                CREATE INDEX IF NOT EXISTS ix_ferias_periodos_wpf_ano
                    ON ferias_periodos_wpf(ano, indice, id);

                CREATE TABLE IF NOT EXISTS ferias_alocacoes_wpf(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ano INTEGER NOT NULL,
                    periodo_id INTEGER NOT NULL,
                    militar_id INTEGER NOT NULL,
                    dias INTEGER NOT NULL DEFAULT 30,
                    pago INTEGER NOT NULL DEFAULT 0,
                    pago_em TEXT,
                    aux_alimentacao_pago INTEGER NOT NULL DEFAULT 0,
                    aux_alimentacao_pago_em TEXT,
                    criado_em TEXT,
                    atualizado_em TEXT,
                    UNIQUE(ano, periodo_id, militar_id),
                    FOREIGN KEY(periodo_id) REFERENCES ferias_periodos_wpf(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS ix_ferias_alocacoes_wpf_ano_periodo
                    ON ferias_alocacoes_wpf(ano, periodo_id, militar_id);
                CREATE INDEX IF NOT EXISTS ix_ferias_alocacoes_wpf_militar
                    ON ferias_alocacoes_wpf(ano, militar_id);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureColumnAsync(connection, "ferias_alocacoes_wpf", "pago_em", "TEXT", cancellationToken);
            await EnsureColumnAsync(connection, "ferias_alocacoes_wpf", "aux_alimentacao_pago", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "ferias_alocacoes_wpf", "aux_alimentacao_pago_em", "TEXT", cancellationToken);
            await TryMigrateLegacyAsync(connection, cancellationToken);
            await RepairLegacyPeriodDatesAsync(connection, cancellationToken);
            _schemaReady = true;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha preparando o Plano de Férias nativo.", ex);
            throw;
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<List<string>> ColumnsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info([{table.Replace("]", "]]", StringComparison.Ordinal)}]);";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(1));
        return result;
    }


    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string typeSql, CancellationToken cancellationToken)
    {
        var columns = await ColumnsAsync(connection, table, cancellationToken);
        if (columns.Contains(column, StringComparer.OrdinalIgnoreCase)) return;
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ALTER TABLE {Q(table)} ADD COLUMN {Q(column)} {typeSql};";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Pick(IReadOnlyCollection<string> columns, params string[] candidates)
        => candidates.FirstOrDefault(c => columns.Contains(c, StringComparer.OrdinalIgnoreCase));

    private static string Q(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static async Task<int> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Q(table)};";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task TryMigrateLegacyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (await CountAsync(connection, "ferias_periodos_wpf", cancellationToken) > 0) return;
        if (!await TableExistsAsync(connection, "ferias_periodos", cancellationToken)) return;
        try
        {
            var pCols = await ColumnsAsync(connection, "ferias_periodos", cancellationToken);
            var id = Pick(pCols, "id", "periodo_id");
            var year = Pick(pCols, "ano", "year");
            var index = Pick(pCols, "idx", "indice", "ordem", "numero");
            var name = Pick(pCols, "nome", "name", "titulo");
            var start = Pick(pCols, "data_inicio", "inicio", "di");
            var end = Pick(pCols, "data_fim", "fim", "df");
            var special = Pick(pCols, "especial", "is_especial");
            if (id is null || year is null || index is null) return;

            var legacyPeriods = new List<(int OldId, int Year, int Index, string Name, string? Start, string? End, int Special)>();
            var select = $"SELECT {Q(id)}, {Q(year)}, {Q(index)}, {(name is null ? "''" : Q(name))}, {(start is null ? "NULL" : Q(start))}, {(end is null ? "NULL" : Q(end))}, {(special is null ? "0" : Q(special))} FROM ferias_periodos;";
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = select;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    legacyPeriods.Add((
                        Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                        Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                        Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                        reader.IsDBNull(3) ? string.Empty : reader.GetValue(3)?.ToString() ?? string.Empty,
                        reader.IsDBNull(4) ? null : reader.GetValue(4)?.ToString(),
                        reader.IsDBNull(5) ? null : reader.GetValue(5)?.ToString(),
                        reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture)));
                }
            }

            var idMap = new Dictionary<int, int>();
            foreach (var period in legacyPeriods)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO ferias_periodos_wpf(ano,indice,nome,data_inicio,data_fim,especial,criado_em,atualizado_em)
                    VALUES($ano,$indice,$nome,$inicio,$fim,$especial,$agora,$agora);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$ano", period.Year);
                insert.Parameters.AddWithValue("$indice", period.Index);
                insert.Parameters.AddWithValue("$nome", period.Name);
                insert.Parameters.AddWithValue("$inicio", (object?)period.Start ?? DBNull.Value);
                insert.Parameters.AddWithValue("$fim", (object?)period.End ?? DBNull.Value);
                insert.Parameters.AddWithValue("$especial", period.Special);
                insert.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                idMap[period.OldId] = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }

            if (idMap.Count == 0 || !await TableExistsAsync(connection, "ferias_alocacoes", cancellationToken)) return;
            var aCols = await ColumnsAsync(connection, "ferias_alocacoes", cancellationToken);
            var aYear = Pick(aCols, "ano", "year");
            var aPeriod = Pick(aCols, "periodo_id", "id_periodo");
            var aMilitary = Pick(aCols, "militar_id", "id_militar");
            var aDays = Pick(aCols, "dias", "days");
            var aPaid = Pick(aCols, "pago", "paid");
            if (aYear is null || aPeriod is null || aMilitary is null) return;

            var legacyAllocations = new List<(int Year, int PeriodId, int MilitaryId, int Days, int Paid)>();
            await using (var readA = connection.CreateCommand())
            {
                readA.CommandText = $"SELECT {Q(aYear)}, {Q(aPeriod)}, {Q(aMilitary)}, {(aDays is null ? "30" : Q(aDays))}, {(aPaid is null ? "0" : Q(aPaid))} FROM ferias_alocacoes;";
                await using var rr = await readA.ExecuteReaderAsync(cancellationToken);
                while (await rr.ReadAsync(cancellationToken))
                {
                    legacyAllocations.Add((
                        Convert.ToInt32(rr.GetValue(0), CultureInfo.InvariantCulture),
                        Convert.ToInt32(rr.GetValue(1), CultureInfo.InvariantCulture),
                        Convert.ToInt32(rr.GetValue(2), CultureInfo.InvariantCulture),
                        rr.IsDBNull(3) ? 30 : Convert.ToInt32(rr.GetValue(3), CultureInfo.InvariantCulture),
                        rr.IsDBNull(4) ? 0 : Convert.ToInt32(rr.GetValue(4), CultureInfo.InvariantCulture)));
                }
            }

            foreach (var allocation in legacyAllocations)
            {
                if (!idMap.TryGetValue(allocation.PeriodId, out var newPeriod)) continue;
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT OR IGNORE INTO ferias_alocacoes_wpf(ano,periodo_id,militar_id,dias,pago,criado_em,atualizado_em)
                    VALUES($ano,$periodo,$militar,$dias,$pago,$agora,$agora);
                    """;
                insert.Parameters.AddWithValue("$ano", allocation.Year);
                insert.Parameters.AddWithValue("$periodo", newPeriod);
                insert.Parameters.AddWithValue("$militar", allocation.MilitaryId);
                insert.Parameters.AddWithValue("$dias", allocation.Days);
                insert.Parameters.AddWithValue("$pago", allocation.Paid);
                insert.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch
        {
            // Migração é uma cortesia. Qualquer variação antiga desconhecida não pode impedir o módulo novo de abrir.
        }
    }

    private static async Task RepairLegacyPeriodDatesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Versões antigas podiam migrar parcialmente os períodos: uma data ficava
        // no banco legado e a outra no banco WPF. A rotina anterior não voltava a
        // consultar a tabela antiga depois que o primeiro registro era criado.
        // Esta reconciliação preenche somente campos vazios, preservando toda edição
        // já feita no módulo novo.
        if (!await TableExistsAsync(connection, "ferias_periodos", cancellationToken)) return;
        try
        {
            var columns = await ColumnsAsync(connection, "ferias_periodos", cancellationToken);
            var year = Pick(columns, "ano", "year");
            var index = Pick(columns, "idx", "indice", "ordem", "numero");
            var name = Pick(columns, "nome", "name", "titulo");
            var start = Pick(columns, "data_inicio", "inicio", "di");
            var end = Pick(columns, "data_fim", "fim", "df");
            var special = Pick(columns, "especial", "is_especial");
            if (year is null || index is null || (start is null && end is null)) return;

            var legacy = new List<(int Year, int Index, string Name, string? Start, string? End, int Special)>();
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = $"SELECT {Q(year)}, {Q(index)}, {(name is null ? "''" : Q(name))}, {(start is null ? "NULL" : Q(start))}, {(end is null ? "NULL" : Q(end))}, {(special is null ? "0" : Q(special))} FROM ferias_periodos;";
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    legacy.Add((
                        Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                        Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                        reader.IsDBNull(2) ? string.Empty : reader.GetValue(2)?.ToString() ?? string.Empty,
                        reader.IsDBNull(3) ? null : NormalizeLegacyDate(reader.GetValue(3)?.ToString()),
                        reader.IsDBNull(4) ? null : NormalizeLegacyDate(reader.GetValue(4)?.ToString()),
                        reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture)));
                }
            }

            foreach (var item in legacy)
            {
                await using var find = connection.CreateCommand();
                find.CommandText = "SELECT id,nome,data_inicio,data_fim FROM ferias_periodos_wpf WHERE ano=$ano AND indice=$indice AND especial=$especial ORDER BY id LIMIT 1;";
                find.Parameters.AddWithValue("$ano", item.Year);
                find.Parameters.AddWithValue("$indice", item.Index);
                find.Parameters.AddWithValue("$especial", item.Special);
                int id = 0;
                string currentName = string.Empty, currentStart = string.Empty, currentEnd = string.Empty;
                await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
                {
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        id = reader.GetInt32(0);
                        currentName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        currentStart = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                        currentEnd = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    }
                }

                if (id == 0)
                {
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO ferias_periodos_wpf(ano,indice,nome,data_inicio,data_fim,especial,criado_em,atualizado_em)
                        VALUES($ano,$indice,$nome,$inicio,$fim,$especial,$agora,$agora);
                        """;
                    insert.Parameters.AddWithValue("$ano", item.Year);
                    insert.Parameters.AddWithValue("$indice", Math.Max(1, item.Index));
                    insert.Parameters.AddWithValue("$nome", string.IsNullOrWhiteSpace(item.Name) ? $"{item.Index}º Período" : item.Name);
                    insert.Parameters.AddWithValue("$inicio", (object?)item.Start ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$fim", (object?)item.End ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$especial", item.Special);
                    insert.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                    try { await insert.ExecuteNonQueryAsync(cancellationToken); } catch (SqliteException) { }
                    continue;
                }

                var nextName = string.IsNullOrWhiteSpace(currentName) ? item.Name : currentName;
                var nextStart = string.IsNullOrWhiteSpace(currentStart) ? item.Start : NormalizeLegacyDate(currentStart);
                var nextEnd = string.IsNullOrWhiteSpace(currentEnd) ? item.End : NormalizeLegacyDate(currentEnd);
                if (nextName == currentName && nextStart == currentStart && nextEnd == currentEnd) continue;

                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE ferias_periodos_wpf SET nome=$nome,data_inicio=$inicio,data_fim=$fim,atualizado_em=$agora WHERE id=$id;";
                update.Parameters.AddWithValue("$nome", nextName ?? string.Empty);
                update.Parameters.AddWithValue("$inicio", (object?)nextStart ?? DBNull.Value);
                update.Parameters.AddWithValue("$fim", (object?)nextEnd ?? DBNull.Value);
                update.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch
        {
            // Bancos muito antigos podem ter estruturas diferentes. O módulo novo
            // continua abrindo mesmo quando não há nada seguro para reconciliar.
        }
    }

    private static string? NormalizeLegacyDate(string? value)
    {
        var parsed = ParseDate(value);
        return parsed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
               ?? (string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public async Task<IReadOnlyList<VacationPeriod>> GetPeriodsAsync(int year, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureDefaultPeriodsAsync(year, cancellationToken);
        var result = new List<VacationPeriod>();
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,ano,indice,nome,data_inicio,data_fim,especial FROM ferias_periodos_wpf WHERE ano=$ano ORDER BY especial,indice,id;";
        cmd.Parameters.AddWithValue("$ano", year);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadPeriod(reader));
        return result;
    }

    private async Task EnsureDefaultPeriodsAsync(int year, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        for (var index = 1; index <= 9; index++)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO ferias_periodos_wpf(ano,indice,nome,data_inicio,data_fim,especial,criado_em,atualizado_em)
                VALUES($ano,$indice,$nome,NULL,NULL,0,$agora,$agora);
                """;
            cmd.Parameters.AddWithValue("$ano", year);
            cmd.Parameters.AddWithValue("$indice", index);
            cmd.Parameters.AddWithValue("$nome", $"{index}º Período");
            cmd.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    private static VacationPeriod ReadPeriod(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        Year = reader.GetInt32(1),
        Index = reader.GetInt32(2),
        Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
        StartDate = ParseDate(reader.IsDBNull(4) ? null : reader.GetString(4)),
        EndDate = ParseDate(reader.IsDBNull(5) ? null : reader.GetString(5)),
        IsSpecial = !reader.IsDBNull(6) && reader.GetInt32(6) != 0
    };

    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "O", "s" };
        return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value)
            ? value.Date
            : DateTime.TryParse(text, PtBr, DateTimeStyles.AllowWhiteSpaces, out value) ? value.Date : null;
    }

    public async Task SavePeriodAsync(VacationPeriod period, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        if (period.Year < 2000 || period.Year > 2200) throw new InvalidOperationException("Informe um ano válido.");
        if (period.StartDate is not null && period.EndDate is not null && period.EndDate < period.StartDate)
            throw new InvalidOperationException("A data final não pode ser anterior à data inicial.");
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        if (period.Id <= 0)
        {
            cmd.CommandText = """
                INSERT INTO ferias_periodos_wpf(ano,indice,nome,data_inicio,data_fim,especial,criado_em,atualizado_em)
                VALUES($ano,$indice,$nome,$inicio,$fim,$especial,$agora,$agora);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE ferias_periodos_wpf SET indice=$indice,nome=$nome,data_inicio=$inicio,data_fim=$fim,especial=$especial,atualizado_em=$agora
                WHERE id=$id AND ano=$ano;
                SELECT $id;
                """;
            cmd.Parameters.AddWithValue("$id", period.Id);
        }
        cmd.Parameters.AddWithValue("$ano", period.Year);
        cmd.Parameters.AddWithValue("$indice", Math.Max(1, period.Index));
        cmd.Parameters.AddWithValue("$nome", string.IsNullOrWhiteSpace(period.Name) ? $"{Math.Max(1, period.Index)}º Período" : period.Name.Trim());
        cmd.Parameters.AddWithValue("$inicio", period.StartDate is null ? DBNull.Value : period.StartDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$fim", period.EndDate is null ? DBNull.Value : period.EndDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$especial", period.IsSpecial ? 1 : 0);
        cmd.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
        period.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<VacationPeriod> CreateSpecialPeriodAsync(int year, CancellationToken cancellationToken = default)
    {
        var periods = await GetPeriodsAsync(year, cancellationToken);
        var next = Math.Max(10, periods.Select(x => x.Index).DefaultIfEmpty(9).Max() + 1);
        var period = new VacationPeriod { Year = year, Index = next, Name = "Período Especial", IsSpecial = true };
        await SavePeriodAsync(period, cancellationToken);
        return period;
    }

    public async Task DeleteSpecialPeriodAsync(int periodId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ferias_periodos_wpf WHERE id=$id AND especial=1;";
        cmd.Parameters.AddWithValue("$id", periodId);
        if (await cmd.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("Somente períodos especiais podem ser excluídos.");
    }

    public async Task<IReadOnlyList<VacationAllocation>> GetAllocationsAsync(int year, int? periodId = null, CancellationToken cancellationToken = default)
    {
        var rows = await _military.GetAllAsync(cancellationToken);
        return await GetAllocationsAsync(year, periodId, rows.ToDictionary(x => x.Id), cancellationToken);
    }

    public async Task<IReadOnlyList<VacationAllocation>> GetAllocationsAsync(
        int year,
        int? periodId,
        IReadOnlyDictionary<int, MilitaryRecord> militaryLookup,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<VacationAllocation>();
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,ano,periodo_id,militar_id,dias,pago,pago_em,aux_alimentacao_pago,aux_alimentacao_pago_em FROM ferias_alocacoes_wpf WHERE ano=$ano" + (periodId is null ? string.Empty : " AND periodo_id=$periodo") + ";";
        cmd.Parameters.AddWithValue("$ano", year);
        if (periodId is not null) cmd.Parameters.AddWithValue("$periodo", periodId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var militaryId = reader.GetInt32(3);
            if (!militaryLookup.TryGetValue(militaryId, out var item)) continue;
            result.Add(new VacationAllocation
            {
                Id = reader.GetInt32(0),
                Year = reader.GetInt32(1),
                PeriodId = reader.GetInt32(2),
                MilitaryId = militaryId,
                Days = Math.Clamp(reader.GetInt32(4), 1, 30),
                IsPaid = reader.GetInt32(5) != 0,
                PaidAt = reader.IsDBNull(6) ? null : ParseDbDate(reader.GetString(6)),
                FoodAidPaid = !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
                FoodAidPaidAt = reader.IsDBNull(8) ? null : ParseDbDate(reader.GetString(8)),
                Military = item
            });
        }
        return result
            .OrderBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
            .ThenBy(x => x.Military.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<VacationAllocation>> GetAllocationsForMilitaryAsync(
        int year,
        MilitaryRecord military,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<VacationAllocation>();
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id,ano,periodo_id,militar_id,dias,pago,pago_em,aux_alimentacao_pago,aux_alimentacao_pago_em FROM ferias_alocacoes_wpf WHERE ano=$ano AND militar_id=$militar ORDER BY periodo_id;";
        cmd.Parameters.AddWithValue("$ano", year);
        cmd.Parameters.AddWithValue("$militar", military.Id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new VacationAllocation
            {
                Id = reader.GetInt32(0),
                Year = reader.GetInt32(1),
                PeriodId = reader.GetInt32(2),
                MilitaryId = reader.GetInt32(3),
                Days = Math.Clamp(reader.GetInt32(4), 1, 30),
                IsPaid = reader.GetInt32(5) != 0,
                PaidAt = reader.IsDBNull(6) ? null : ParseDbDate(reader.GetString(6)),
                FoodAidPaid = !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
                FoodAidPaidAt = reader.IsDBNull(8) ? null : ParseDbDate(reader.GetString(8)),
                Military = military
            });
        }
        return result;
    }

    public async Task<Dictionary<int, int>> GetAnnualDaysAsync(int year, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new Dictionary<int, int>();
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT militar_id,COALESCE(SUM(dias),0) FROM ferias_alocacoes_wpf WHERE ano=$ano GROUP BY militar_id;";
        cmd.Parameters.AddWithValue("$ano", year);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetInt32(0)] = reader.GetInt32(1);
        return result;
    }

    public async Task<(int Added, List<string> Failures)> AllocateAsync(int year, int periodId, IEnumerable<MilitaryRecord> selected, int days, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        days = NormalizeDays(days);
        var annual = await GetAnnualDaysAsync(year, cancellationToken);
        var failures = new List<string>();
        var added = 0;
        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO ferias_alocacoes_wpf(ano,periodo_id,militar_id,dias,pago,criado_em,atualizado_em)
            VALUES($ano,$periodo,$militar,$dias,0,$agora,$agora);
            """;
        cmd.Parameters.AddWithValue("$ano", year);
        cmd.Parameters.AddWithValue("$periodo", periodId);
        var militaryParameter = cmd.Parameters.Add("$militar", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$dias", days);
        var nowParameter = cmd.Parameters.Add("$agora", SqliteType.Text);
        await cmd.PrepareAsync(cancellationToken);

        foreach (var item in selected.GroupBy(x => x.Id).Select(x => x.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (annual.GetValueOrDefault(item.Id) + days > 30)
            {
                failures.Add($"{item.ShortRank} {item.Name}: ultrapassaria 30 dias no ano.");
                continue;
            }
            militaryParameter.Value = item.Id;
            nowParameter.Value = DateTime.Now.ToString("O", CultureInfo.InvariantCulture);
            if (await cmd.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                added++;
                annual[item.Id] = annual.GetValueOrDefault(item.Id) + days;
            }
            else failures.Add($"{item.ShortRank} {item.Name}: já está neste período.");
        }
        await tx.CommitAsync(cancellationToken);
        return (added, failures);
    }

    public async Task MoveAllocationAsync(VacationAllocation allocation, int destinationPeriodId, int days, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        days = NormalizeDays(days);
        await using var connection = OpenConnection();
        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT COALESCE(SUM(dias),0) FROM ferias_alocacoes_wpf WHERE ano=$ano AND militar_id=$militar AND id<>$id;";
        check.Parameters.AddWithValue("$ano", allocation.Year);
        check.Parameters.AddWithValue("$militar", allocation.MilitaryId);
        check.Parameters.AddWithValue("$id", allocation.Id);
        var otherDays = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (otherDays + days > 30) throw new InvalidOperationException("A alteração ultrapassaria 30 dias de férias no ano.");
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ferias_alocacoes_wpf SET periodo_id=$periodo,dias=$dias,atualizado_em=$agora WHERE id=$id;";
        cmd.Parameters.AddWithValue("$periodo", destinationPeriodId);
        cmd.Parameters.AddWithValue("$dias", days);
        cmd.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$id", allocation.Id);
        try { await cmd.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { throw new InvalidOperationException("O militar já está alocado no período de destino.", ex); }
    }

    public async Task RemoveAllocationAsync(int allocationId, CancellationToken cancellationToken = default)
        => await RemoveAllocationsAsync([allocationId], cancellationToken);

    public async Task RemoveAllocationsAsync(IEnumerable<int> allocationIds, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var ids = allocationIds.Distinct().Where(x => x > 0).ToArray();
        if (ids.Length == 0) return;
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var allocationId in ids)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = "DELETE FROM ferias_alocacoes_wpf WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", allocationId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetPaidAnnualAsync(int year, int militaryId, bool paid, bool foodAidConfirmed = false, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        if (paid && await MilitaryRequiresFoodAidAsync(militaryId, cancellationToken) && !foodAidConfirmed)
            throw new InvalidOperationException("Para Cabo/Soldado, confirme também o pagamento do Auxílio-Alimentação de férias.");
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE ferias_alocacoes_wpf SET pago=$pago,pago_em=$pago_em,aux_alimentacao_pago=$aux,aux_alimentacao_pago_em=$aux_em,atualizado_em=$agora WHERE ano=$ano AND militar_id=$militar;";
        var now = DateTime.Now.ToString("O", CultureInfo.InvariantCulture);
        cmd.Parameters.AddWithValue("$pago", paid ? 1 : 0);
        cmd.Parameters.AddWithValue("$pago_em", paid ? (object)now : DBNull.Value);
        cmd.Parameters.AddWithValue("$aux", paid && foodAidConfirmed ? 1 : 0);
        cmd.Parameters.AddWithValue("$aux_em", paid && foodAidConfirmed ? (object)now : DBNull.Value);
        cmd.Parameters.AddWithValue("$agora", now);
        cmd.Parameters.AddWithValue("$ano", year);
        cmd.Parameters.AddWithValue("$militar", militaryId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetPaidAsync(IEnumerable<int> allocationIds, bool paid, bool foodAidConfirmed = false, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var ids = allocationIds.Distinct().ToList();
        if (ids.Count == 0) return;
        if (paid && !foodAidConfirmed && await AllocationsRequireFoodAidAsync(ids, cancellationToken))
            throw new InvalidOperationException("Para Cabo/Soldado, confirme também o pagamento do Auxílio-Alimentação de férias.");
        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in ids)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "UPDATE ferias_alocacoes_wpf SET pago=$pago,pago_em=$pago_em,aux_alimentacao_pago=$aux,aux_alimentacao_pago_em=$aux_em,atualizado_em=$agora WHERE id=$id;";
            var now = DateTime.Now.ToString("O", CultureInfo.InvariantCulture);
            cmd.Parameters.AddWithValue("$pago", paid ? 1 : 0);
            cmd.Parameters.AddWithValue("$pago_em", paid ? (object)now : DBNull.Value);
            cmd.Parameters.AddWithValue("$aux", paid && foodAidConfirmed ? 1 : 0);
            cmd.Parameters.AddWithValue("$aux_em", paid && foodAidConfirmed ? (object)now : DBNull.Value);
            cmd.Parameters.AddWithValue("$agora", now);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    private async Task<bool> MilitaryRequiresFoodAidAsync(int militaryId, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(posto,'') FROM militares WHERE id=$id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", militaryId);
        var rank = Convert.ToString(await cmd.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? string.Empty;
        var normalized = NormalizePlain(MilitaryRankService.Canonicalize(rank));
        return normalized.Contains("CABO", StringComparison.OrdinalIgnoreCase) || normalized.Contains("SOLDADO", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> AllocationsRequireFoodAidAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            await using var connection = OpenConnection();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT a.militar_id FROM ferias_alocacoes_wpf a WHERE a.id=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", id);
            var value = await cmd.ExecuteScalarAsync(cancellationToken);
            if (value is not null && value is not DBNull && await MilitaryRequiresFoodAidAsync(Convert.ToInt32(value, CultureInfo.InvariantCulture), cancellationToken)) return true;
        }
        return false;
    }

    private static DateTime? ParseDbDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
    }

    public static int NormalizeDays(int days) => days switch { <= 10 => 10, <= 15 => 15, _ => 30 };

    public async Task<VacationPreferences> LoadPreferencesAsync()
    {
        var preferences = await _json.LoadAsync<VacationPreferences>(_paths.VacationPreferencesFile) ?? new VacationPreferences();
        var node = await _json.LoadNodeAsync(_paths.VacationPreferencesFile);
        if (node is JsonObject legacy)
        {
            preferences.LastYear = LegacyInt(legacy, "last_year", preferences.LastYear);
            preferences.LastPeriodId = LegacyInt(legacy, "last_periodo_id", preferences.LastPeriodId);
            preferences.LastTab = LegacyInt(legacy, "tab", preferences.LastTab);
            preferences.DefaultDays = NormalizeDays(LegacyInt(legacy, "dias", preferences.DefaultDays));
            preferences.Search = LegacyString(legacy, "filtro_m", preferences.Search);
            preferences.AllocationSearch = LegacyString(legacy, "filtro_a", preferences.AllocationSearch);
            preferences.Rank = LegacyString(legacy, "pg_m", preferences.Rank);
            preferences.SortMode = LegacyString(legacy, "ordenacao", preferences.SortMode);
            preferences.AvailableOnly = LegacyBool(legacy, "apenas_disponiveis", preferences.AvailableOnly);
            preferences.LastModel = LegacyString(legacy, "last_model", preferences.LastModel);
            if (legacy["form_cache"] is JsonObject cache)
                foreach (var pair in cache)
                    if (pair.Value is not null) preferences.FormFields[pair.Key] = pair.Value.ToString();
        }
        return preferences;
    }
    public Task SavePreferencesAsync(VacationPreferences value) => _json.SaveAsync(_paths.VacationPreferencesFile, value);

    public async Task<VacationBulletinStore> LoadBulletinStoreAsync()
    {
        var store = await _json.LoadAsync<VacationBulletinStore>(_paths.VacationBulletinsFile) ?? new VacationBulletinStore();
        var migrated = false;
        if (store.Models.Count == 0 && File.Exists(_paths.VacationLegacyModelsFile))
        {
            if (await _json.LoadNodeAsync(_paths.VacationLegacyModelsFile) is JsonObject legacyModels)
            {
                foreach (var pair in legacyModels)
                    if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(pair.Key))
                    {
                        if (pair.Key is "Férias - Ordem de Saque" or "Férias - Saque de Atrasados") continue;
                        store.Models[pair.Key] = text ?? string.Empty;
                        migrated = true;
                    }
            }
        }
        if (store.Saved.Count == 0 && File.Exists(_paths.VacationLegacyGeneratedFile))
        {
            if (await _json.LoadNodeAsync(_paths.VacationLegacyGeneratedFile) is JsonObject generated && generated["items"] is JsonArray items)
            {
                foreach (var raw in items.OfType<JsonObject>())
                {
                    var created = ParseLegacyDate(LegacyString(raw, "created_at", string.Empty));
                    store.Saved.Add(new VacationSavedBulletin
                    {
                        Id = LegacyString(raw, "id", DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)),
                        Title = LegacyString(raw, "title", "Boletim"),
                        Text = LegacyString(raw, "text", string.Empty),
                        CreatedAt = created,
                        UpdatedAt = created
                    });
                    migrated = true;
                }
            }
        }
        foreach (var pair in DefaultModels())
        {
            if (IsVacationBuiltInModel(pair.Key))
            {
                // Mantém os modelos oficiais de férias atualizados mesmo quando já havia cache antigo no AppData.
                store.Models[pair.Key] = pair.Value;
                migrated = true;
            }
            else if (!store.Models.TryGetValue(pair.Key, out var text) || string.IsNullOrWhiteSpace(text))
            {
                store.Models[pair.Key] = pair.Value;
            }
        }
        if (migrated) await SaveBulletinStoreAsync(store);
        return store;
    }

    private static bool IsVacationBuiltInModel(string key)
        => key.StartsWith("ADICIONAL FERIAS -", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("ANTECIPAÇÃO 1ª PARCELA ADICIONAL NATALINO - Férias", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("INDENIZAÇÃO DE FÉRIAS -", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("ADICIONAL DE FÉRIAS -", StringComparison.OrdinalIgnoreCase)
           || key.StartsWith("AUXÍLIO-ALIMENTAÇÃO -", StringComparison.OrdinalIgnoreCase);

    private static int LegacyInt(JsonObject node, string key, int fallback)
        => node[key] is JsonValue value && value.TryGetValue<int>(out var result) ? result : fallback;
    private static bool LegacyBool(JsonObject node, string key, bool fallback)
        => node[key] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;
    private static string LegacyString(JsonObject node, string key, string fallback)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result ?? fallback : fallback;
    private static DateTime ParseLegacyDate(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : DateTime.Now;
    public Task SaveBulletinStoreAsync(VacationBulletinStore value) => _json.SaveAsync(_paths.VacationBulletinsFile, value);

    public async Task<Dictionary<string, VacationFinancialProfile>> LoadFinancialProfilesAsync()
    {
        var result = await _json.LoadAsync<Dictionary<string, VacationFinancialProfile>>(_paths.VacationFinancialFile)
                     ?? new Dictionary<string, VacationFinancialProfile>(StringComparer.OrdinalIgnoreCase);
        var node = await _json.LoadNodeAsync(_paths.VacationFinancialFile);
        if (node is not JsonObject root) return result;
        foreach (var pair in root)
        {
            if (pair.Value is not JsonObject item) continue;
            var parts = pair.Key.Split(':');
            var year = parts.Length == 2 && int.TryParse(parts[0], out var parsedYear) ? parsedYear : LegacyInt(item, "ano", 0);
            var militaryId = parts.Length == 2 && int.TryParse(parts[1], out var parsedId) ? parsedId
                : int.TryParse(pair.Key, out var legacyId) ? legacyId : LegacyInt(item, "militar_id", 0);
            var profile = new VacationFinancialProfile
            {
                MilitaryId = militaryId,
                Year = year,
                Type = LegacyString(item, "tipo", LegacyString(item, "Type", "Temporário")),
                QualificationPercent = LegacyPercent(item, "pct_habilitacao") ?? LegacyDecimal(item, "QualificationPercent"),
                MilitaryAdditionalPercent = LegacyPercent(item, "pct_adicional_militar") ?? LegacyDecimal(item, "MilitaryAdditionalPercent"),
                AvailabilityPercent = LegacyPercent(item, "pct_disponibilidade") ?? LegacyDecimal(item, "AvailabilityPercent")
            };
            if (militaryId > 0) result[pair.Key] = profile;
        }
        return result;
    }

    private static decimal? LegacyPercent(JsonObject node, string key)
    {
        var raw = node[key]?.ToString()?.Replace("%", string.Empty, StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return decimal.TryParse(raw, NumberStyles.Number, PtBr, out var value) ||
               decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value) ? value : null;
    }
    private static decimal? LegacyDecimal(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<decimal>(out var parsed) ? parsed : null;

    public Task SaveFinancialProfilesAsync(Dictionary<string, VacationFinancialProfile> profiles)
        => _json.SaveAsync(_paths.VacationFinancialFile, profiles);

    public static string FinancialKey(int militaryId, int year) => $"{year}:{militaryId}";

    public VacationFinancialProfile CreateDefaultFinancialProfile(MilitaryRecord military, int year)
    {
        var defaults = DefaultPercentages(military.Rank);
        return new VacationFinancialProfile
        {
            MilitaryId = military.Id,
            Year = year,
            Type = defaults.Type,
            QualificationPercent = defaults.Qualification,
            MilitaryAdditionalPercent = defaults.Military,
            AvailabilityPercent = defaults.Availability
        };
    }

    public async Task<VacationFinancialProfile> GetFinancialProfileAsync(MilitaryRecord military, int year)
    {
        var profiles = await LoadFinancialProfilesAsync();
        if (profiles.TryGetValue(FinancialKey(military.Id, year), out var profile)) return profile;
        if (profiles.TryGetValue(military.Id.ToString(CultureInfo.InvariantCulture), out profile))
        {
            profile.MilitaryId = military.Id;
            profile.Year = year;
            return profile;
        }
        return CreateDefaultFinancialProfile(military, year);
    }

    public async Task SaveFinancialProfileAsync(VacationFinancialProfile profile)
    {
        var profiles = await LoadFinancialProfilesAsync();
        profile.UpdatedAt = DateTime.Now;
        profiles[FinancialKey(profile.MilitaryId, profile.Year)] = profile;
        await SaveFinancialProfilesAsync(profiles);
    }

    public async Task<VacationFinancialResult> CalculateFinancialAsync(MilitaryRecord military, int year, VacationFinancialProfile? profile = null, CancellationToken cancellationToken = default)
    {
        profile ??= await GetFinancialProfileAsync(military, year);
        var salary = await _military.GetSalaryByRankAsync(military.Rank, cancellationToken);
        if (salary <= 0) return new VacationFinancialResult { Error = $"Soldo não encontrado para {military.ShortRank}. Cadastre em Soldos por Posto." };
        if (profile.QualificationPercent is null || profile.MilitaryAdditionalPercent is null || profile.AvailabilityPercent is null)
            return new VacationFinancialResult { Error = "Preencha os três percentuais da carteira individual." };
        decimal Add(decimal percent) => Math.Round(salary * percent / 100m, 2, MidpointRounding.AwayFromZero);
        var qualification = Add(profile.QualificationPercent.Value);
        var militaryAdditional = Add(profile.MilitaryAdditionalPercent.Value);
        var availability = Add(profile.AvailabilityPercent.Value);
        var baseTotal = salary + qualification + militaryAdditional + availability;
        return new VacationFinancialResult
        {
            Success = true, Salary = salary, QualificationAdditional = qualification,
            MilitaryAdditional = militaryAdditional, AvailabilityAdditional = availability,
            BaseTotal = baseTotal, VacationAdditional = Math.Round(baseTotal / 3m, 2, MidpointRounding.AwayFromZero)
        };
    }

    private static (string Type, decimal? Qualification, decimal? Military, decimal? Availability) DefaultPercentages(string? rank)
    {
        var canonical = MilitaryRankService.Canonicalize(rank);
        var officer = canonical is "Aspirante" or "2º Tenente" or "1º Tenente";
        if (officer) return ("Temporário", 12m, 19m, canonical == "1º Tenente" ? 6m : 5m);
        if (canonical is "Cabo Efetivo Profissional") return ("Temporário", 12m, 13m, 6m);
        if (canonical is "3º Sargento" or "Soldado Efetivo Profissional" or "Soldado Efetivo Variável") return ("Temporário", 12m, 13m, 5m);
        return ("Carreira", null, null, null);
    }

    public async Task<string> GeneratePreviewAsync(string model, VacationPeriod period, int year, bool includeLateValues, IReadOnlyDictionary<string, string>? formFields = null, string? modelName = null, CancellationToken cancellationToken = default)
    {
        var allocations = (await GetAllocationsAsync(year, period.Id, cancellationToken))
            .Where(x => !x.IsPaid)
            .OrderBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
            .ThenBy(x => MilitaryRankService.Canonicalize(x.Military.Rank), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Military.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var normalizedModel = NormalizePlain(model);
        var normalizedModelName = NormalizePlain(modelName);
        var modelType = !string.IsNullOrWhiteSpace(normalizedModelName) ? normalizedModelName : normalizedModel;

        // Importante: a identificação do modelo não pode ser feita pelo texto inteiro,
        // porque o modelo de Adicional de Férias pode citar Auxílio-Alimentação apenas
        // como orientação. A filtragem Cb/Sd vale somente para o modelo próprio de
        // AUXÍLIO-ALIMENTAÇÃO; a Ordem de Saque de Férias deve listar TODOS.
        var isVacationFoodAid = modelType.Contains("AUXILIO ALIMENTACAO", StringComparison.OrdinalIgnoreCase);
        if (isVacationFoodAid)
            allocations = allocations.Where(x => IsCaboOuSoldado(x.Military)).ToList();

        var isVacationAdditionalPlan = !isVacationFoodAid
            && (modelType.Contains("ADICIONAL FERIAS", StringComparison.OrdinalIgnoreCase) || modelType.Contains("ADICIONAL DE FERIAS", StringComparison.OrdinalIgnoreCase))
            && !modelType.Contains("DESPESA A ANULAR", StringComparison.OrdinalIgnoreCase)
            && !modelType.Contains("RESERVA", StringComparison.OrdinalIgnoreCase);

        var isVacationLatePay = !isVacationFoodAid
            && (modelType.Contains("ADICIONAL FERIAS", StringComparison.OrdinalIgnoreCase) || modelType.Contains("ADICIONAL DE FERIAS", StringComparison.OrdinalIgnoreCase))
            && modelType.Contains("SAQUE", StringComparison.OrdinalIgnoreCase)
            && modelType.Contains("ATRASAD", StringComparison.OrdinalIgnoreCase);
        var mustShowIndividualVacationValue = includeLateValues || isVacationLatePay;

        const decimal foodAidDailyValue = 13.50m;
        var foodAidCode = modelType.Contains("ATRASAD", StringComparison.OrdinalIgnoreCase)
            ? "A48 - Aux Alim atrasado 1X"
            : "A58 - Aux Alim 1X";

        var blocks = new List<string>();
        foreach (var allocation in allocations)
        {
            var first = $"{allocation.Military.ShortRank} {allocation.Military.Name.ToUpperInvariant()}".Trim();
            var second = $"Prec-CP {Digits(allocation.Military.PrecCp)} CPF {FormatCpf(allocation.Military.Cpf)}";
            var block = first + Environment.NewLine + second;
            if (isVacationFoodAid)
            {
                var totalFoodAid = Math.Round(foodAidDailyValue * Math.Max(allocation.Days, 0), 2, MidpointRounding.AwayFromZero);
                block += Environment.NewLine + $"Ano de referência: {year - 1}";
                block += Environment.NewLine + $"Período de férias: {period.Name} ({FormatDate(period.StartDate)} a {FormatDate(period.EndDate)})";
                block += Environment.NewLine + $"Quantidade de dias: {allocation.Days:00}";
                block += Environment.NewLine + $"Código: {foodAidCode}";
                block += Environment.NewLine + $"Valor por dia: R$ {foodAidDailyValue.ToString("N2", PtBr)}";
                block += Environment.NewLine + $"Valor solicitado: R$ {totalFoodAid.ToString("N2", PtBr)} ({NumberToWordsService.Convert(totalFoodAid, true)})";
            }
            else if (mustShowIndividualVacationValue)
            {
                var result = await CalculateFinancialAsync(allocation.Military, year, null, cancellationToken);
                block += Environment.NewLine + $"Período: {period.Name} ({FormatDate(period.StartDate)} a {FormatDate(period.EndDate)})";
                block += Environment.NewLine + $"Ano de referência: {year - 1}";
                block += Environment.NewLine + $"Quantidade: {VacationMonthsText(allocation.Days)} · {allocation.Days:00} dia(s) de férias";
                if (result.Success)
                {
                    block += Environment.NewLine + $"Valor solicitado: {result.VacationAdditionalText} ({result.VacationAdditionalWords})";
                }
                else
                {
                    block += Environment.NewLine + $"Valor solicitado: NÃO CALCULADO — abrir a Carteira de Férias do militar e preencher os percentuais/soldo. {result.Error}";
                }
            }
            blocks.Add(block);
        }

        var list = blocks.Count == 0 ? (isVacationFoodAid ? "(sem Cabos/Soldados não pagos neste período)" : "(sem militares não pagos)") : string.Join(Environment.NewLine + Environment.NewLine, blocks);
        var distinctDays = allocations.Select(x => x.Days).Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
        var daysText = distinctDays.Count == 1 ? distinctDays[0].ToString(CultureInfo.InvariantCulture) : "variados";

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LISTA"] = list,
            ["ANO"] = year.ToString(CultureInfo.InvariantCulture),
            ["ANO_REF"] = (year - 1).ToString(CultureInfo.InvariantCulture),
            ["DIAS"] = daysText,
            ["PERIODO"] = period.Name,
            ["DATA_INICIO"] = period.StartDate?.ToString("dd/MM/yyyy") ?? string.Empty,
            ["DATA_FIM"] = period.EndDate?.ToString("dd/MM/yyyy") ?? string.Empty,
            ["DATA_INICIO_ABREV"] = FormatDate(period.StartDate),
            ["DATA_FIM_ABREV"] = FormatDate(period.EndDate),
            ["QUANTIDADE"] = allocations.Count.ToString(CultureInfo.InvariantCulture),
            ["VALOR_ATRASADO"] = "conforme valor individual calculado na Carteira de Férias",
            ["VALOR_ATRASADO_EXTENSO"] = "conforme valor individual calculado na Carteira de Férias",
            ["TEXTO_VALOR_ATRASADO"] = "nos valores individuais calculados na Carteira de Férias de cada militar"
        };
        if (formFields is not null) foreach (var pair in formFields) values[pair.Key] = pair.Value;
        var output = string.IsNullOrWhiteSpace(model) ? "{LISTA}" : model;
        foreach (var pair in values) output = ReplaceKey(output, pair.Key, pair.Value);

        return output.Trim();
    }

    public sealed record VacationComplementaryBulletin(string Title, string Text, IReadOnlyList<MilitaryRecord> Military, string? SisbolSubject = null);

    public async Task<IReadOnlyList<VacationComplementaryBulletin>> GenerateComplementaryBulletinsAsync(
        string? model,
        string? modelName,
        VacationPeriod period,
        int year,
        IReadOnlyDictionary<string, string>? formFields = null,
        CancellationToken cancellationToken = default)
    {
        var modelType = NormalizePlain(!string.IsNullOrWhiteSpace(modelName) ? modelName : model);
        if (!IsVacationMainSisbolModel(modelType)) return Array.Empty<VacationComplementaryBulletin>();

        var allocations = (await GetAllocationsAsync(year, period.Id, cancellationToken))
            .Where(x => !x.IsPaid)
            .OrderBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
            .ThenBy(x => x.Military.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var values = formFields is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(formFields, StringComparer.OrdinalIgnoreCase);

        var late = modelType.Contains("SAQUE", StringComparison.OrdinalIgnoreCase)
                   && modelType.Contains("ATRASAD", StringComparison.OrdinalIgnoreCase);

        var result = new List<VacationComplementaryBulletin>();
        var cbSd = allocations.Where(x => IsCaboOuSoldado(x.Military)).ToList();
        if (cbSd.Count > 0)
        {
            var title = late
                ? "AUXÍLIO-ALIMENTAÇÃO - Saque de Atrasados (Cb e Sd Férias)"
                : "AUXÍLIO-ALIMENTAÇÃO - Ordem de Saque (Cb e Sd Férias)";
            var sisbolSubject = late
                ? "AUXILIO-ALIMENTAÇÃO - Saque de atrasado"
                : "AUXILIO-ALIMENTAÇÃO - Ordem de Saque (Cb e Sd Férias)";
            result.Add(new VacationComplementaryBulletin(
                title,
                BuildVacationFoodAidBlock(cbSd, period, year, values, late),
                cbSd.Select(x => x.Military).ToList(),
                sisbolSubject));
        }

        var transport = allocations.Where(x => ReceivesVacationTransportAid(x.Military)).ToList();
        if (transport.Count > 0)
        {
            const string title = "AUXILIO-TRANSPORTE - Despesa a anular - Férias";
            const string sisbolSubject = "AUXILIO-TRANSPORTE - Despesa a anular - Férias";
            result.Add(new VacationComplementaryBulletin(
                title,
                BuildVacationTransportDaBlock(transport, period, year),
                transport.Select(x => x.Military).ToList(),
                sisbolSubject));
        }

        return result;
    }

    private static bool IsVacationMainSisbolModel(string modelType)
    {
        // O Plano de Férias pode usar modelos antigos/salvos com nomes diferentes
        // (ex.: "Férias - Ordem de Saque", "ADICIONAL FERIAS - Ordem de Saque").
        // Para o envio ao SisBol, qualquer publicação principal de férias deve gerar
        // as publicações complementares obrigatórias, sem recursão quando o usuário
        // estiver enviando os próprios complementares.
        if (string.IsNullOrWhiteSpace(modelType)) return false;
        if (!modelType.Contains("FERIAS", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelType.Contains("AUXILIO ALIMENTACAO", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelType.Contains("AUXILIO TRANSPORTE", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelType.Contains("AUXILIO TRASNPORTE", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelType.Contains("DESPESA A ANULAR", StringComparison.OrdinalIgnoreCase)
            || modelType.Contains("DESPESA ANULAR", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelType.Contains("INDENIZACAO", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelType.Contains("RESERVA", StringComparison.OrdinalIgnoreCase)) return false;
        if (modelType.Contains("NATALINO", StringComparison.OrdinalIgnoreCase)) return false;
        return modelType.Contains("ADICIONAL", StringComparison.OrdinalIgnoreCase)
            || modelType.Contains("ORDEM DE SAQUE", StringComparison.OrdinalIgnoreCase)
            || modelType.Contains("SAQUE DE ATRAS", StringComparison.OrdinalIgnoreCase)
            || modelType.Equals("FERIAS", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDate(DateTime? date)
        => date is null ? string.Empty : date.Value.ToString("dd MMM yy", PtBr).Replace(".", string.Empty, StringComparison.Ordinal).ToUpper(PtBr);

    private static string VacationMonthsText(int days)
        => days >= 30 ? "12 (doze) meses" : days >= 15 ? "6 (seis) meses" : days > 0 ? "proporcional ao período" : "não informado";

    private static bool ShouldAppendVacationFoodAid(string? model)
    {
        var text = NormalizePlain(model);
        if (!text.Contains("ADICIONAL FERIAS", StringComparison.OrdinalIgnoreCase) && !text.Contains("ADICIONAL DE FERIAS", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Contains("AUXILIO ALIMENTACAO", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Contains("DESPESA A ANULAR", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Contains("INDENIZACAO", StringComparison.OrdinalIgnoreCase) || text.Contains("RESERVA REMUNERADA", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool IsCaboOuSoldado(MilitaryRecord military)
    {
        var rank = NormalizePlain(MilitaryRankService.Canonicalize(military.Rank));
        return rank.Contains("CABO", StringComparison.OrdinalIgnoreCase) || rank.Contains("SOLDADO", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReceivesVacationTransportAid(MilitaryRecord military)
    {
        if (MilitaryRecord.IsYes(military.ReceivesTransportAid)) return true;
        if (military.TransportGrossTotal.GetValueOrDefault() > 0) return true;
        return ParseMoney(military.TransportAidValue) > 0;
    }

    private static string BuildVacationFoodAidBlock(IReadOnlyList<VacationAllocation> allocations, VacationPeriod period, int year, IReadOnlyDictionary<string, string> values, bool late)
    {
        const decimal etapa = 13.50m;
        var code = late ? "A48 - Aux Alim atrasado 1X" : "A58 - Aux Alim 1X";
        var list = string.Join(Environment.NewLine + Environment.NewLine, allocations.Select(x =>
            $"{x.Military.ShortRank} {x.Military.Name.ToUpperInvariant()}".Trim() + Environment.NewLine +
            $"Prec-CP {Digits(x.Military.PrecCp)} CPF {FormatCpf(x.Military.Cpf)}" + Environment.NewLine +
            $"Código: {code} | Dias: {x.Days:00} | Valor por dia: R$ {etapa.ToString("N2", PtBr)} | Valor total: R$ {(etapa * x.Days).ToString("N2", PtBr)}"));
        var distinctDays = allocations.Select(x => x.Days).Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
        var daysText = distinctDays.Count == 1 ? distinctDays[0].ToString(CultureInfo.InvariantCulture) : "variados";
        var daysWords = distinctDays.Count == 1 ? NumberToWordsService.Convert(distinctDays[0], false) : "variados";
        var monthText = period.StartDate?.ToString("MMMM yy", PtBr).ToUpper(PtBr) ?? "{{MES_REFERENCIA:MES=ABREV_ANO}}";
        var startText = period.StartDate?.ToString("dd MMM", PtBr).Replace(".", string.Empty).ToUpper(PtBr) ?? "{{DATA_INICIO:DATA=ABREV}}";
        var endText = period.EndDate?.ToString("dd MMM yy", PtBr).Replace(".", string.Empty).ToUpper(PtBr) ?? "{{DATA_FIM:DATA=ABREV}}";
        var biReference = FirstValue(values, "BI_REFERENCIA", "PUBLICACAO_BI", "REFERENCIA_BOLETIM") is { Length: > 0 } refValue
            ? refValue
            : BuildBiReference(FirstValue(values, "NUM_BI", "BI_NUMERO"));
        var title = late
            ? "AUXÍLIO-ALIMENTAÇÃO - Saque de Atrasados (Cb e Sd Férias)"
            : "AUXÍLIO-ALIMENTAÇÃO - Ordem de Saque (Cb e Sd Férias)";
        var request = late ? "Seja realizado o saque de atrasado" : "Seja realizado o saque";
        var timing = late ? "já gozadas e não pagas na competência devida" : "previstas para gozo";
        return title + Environment.NewLine + Environment.NewLine +
               $"{request} do auxílio-alimentação referente a {daysText} ({daysWords}) dia(s) de férias {timing}, em favor exclusivamente do(s) Cabo(s) e Soldado(s) abaixo nominado(s), no mês/período de {monthText}, no {period.Name} ({startText} a {endText}), férias relativas ao ano de {year - 1}, conforme publicado no {biReference}, e de acordo com o previsto no caput do art. 69 do Decreto nº 4.307, de 18 JUL 02." + Environment.NewLine + Environment.NewLine +
               list.TrimEnd() + Environment.NewLine;
    }

    private static string BuildVacationTransportDaBlock(IReadOnlyList<VacationAllocation> allocations, VacationPeriod period, int year)
    {
        var monthRef = period.StartDate?.ToString("MMMM yyyy", PtBr).ToUpper(PtBr) ?? $"{year}";
        var list = string.Join(Environment.NewLine + Environment.NewLine, allocations.Select(x =>
        {
            var monthly = NormalizeTransportMonthlyValue(ParseMoney(x.Military.TransportAidValue));
            if (monthly <= 0 && x.Military.TransportGrossTotal.GetValueOrDefault() > 0)
                monthly = NormalizeTransportMonthlyValue((decimal)x.Military.TransportGrossTotal.Value);
            var value = x.Days >= 30 ? monthly : Math.Round(monthly * Math.Max(0, x.Days) / 30m, 2, MidpointRounding.AwayFromZero);
            return $"{x.Military.ShortRank} {x.Military.Name.ToUpperInvariant()}".Trim() + Environment.NewLine +
                   $"Prec-CP {Digits(x.Military.PrecCp)} CPF {FormatCpf(x.Military.Cpf)}" + Environment.NewLine +
                   $"Mês/Ano de referência: {monthRef}" + Environment.NewLine +
                   $"Valor mensal cadastrado: R$ {monthly.ToString("N2", PtBr)}" + Environment.NewLine +
                   $"Quantidade de dias de férias: {x.Days:00}" + Environment.NewLine +
                   $"Valor total a ser descontado: R$ {value.ToString("N2", PtBr)} ({NumberToWordsService.Convert(value, true)})";
        }));
        return "AUXILIO-TRANSPORTE - Despesa a anular - Férias" + Environment.NewLine + Environment.NewLine +
               $"Seja realizada a despesa a anular do auxílio-transporte, por motivo de férias relativas ao ano de {year - 1}, em favor do(s) militar(es) abaixo relacionado(s), conforme valor mensal cadastrado no Auxílio-Transporte e proporcionalidade do período de férias do {period.Name}." + Environment.NewLine + Environment.NewLine +
               list.TrimEnd() + Environment.NewLine;
    }

    private static decimal NormalizeTransportMonthlyValue(decimal value)
    {
        // Valores gravados em versões anteriores podiam vir como 28653 quando o correto era 286,53.
        // O auxílio-transporte mensal normalmente fica muito abaixo desse limite; acima disso tratamos como centavos.
        return value > 5000m ? Math.Round(value / 100m, 2, MidpointRounding.AwayFromZero) : value;
    }

    private static decimal ParseMoney(string? value)
    {
        var text = Regex.Replace(value ?? string.Empty, @"[^0-9,.-]", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return 0m;

        // Dados internos do SIGFUR são salvos como invariant (ex.: 286.53).
        // Já valores digitados pelo usuário podem vir em pt-BR (ex.: 286,53 ou 1.286,53).
        var hasComma = text.Contains(',', StringComparison.Ordinal);
        var hasDot = text.Contains('.', StringComparison.Ordinal);
        var culture = hasComma && (!hasDot || text.LastIndexOf(',') > text.LastIndexOf('.'))
            ? PtBr
            : CultureInfo.InvariantCulture;

        if (decimal.TryParse(text, NumberStyles.Number, culture, out var result)) return result;
        if (decimal.TryParse(text, NumberStyles.Number, PtBr, out result)) return result;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out result) ? result : 0m;
    }

    private static string BuildBiReference(string? value)
    {
        var text = PublicationNumberWithoutYear((value ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(text)) return "{{BI_REFERENCIA}}";
        if (NormalizePlain(text).Contains("BI NR", StringComparison.OrdinalIgnoreCase) || NormalizePlain(text).Contains("BOLETIM INTERNO", StringComparison.OrdinalIgnoreCase))
            return RemoveYearFromPublicationReference(text);
        return $"BI Nrº {text}, da 4ª Cia PE";
    }

    private static string PublicationNumberWithoutYear(string? value)
        => Regex.Replace((value ?? string.Empty).Trim(), @"\b(?<num>\d{1,5})\s*/\s*(?:20)?\d{2}\b", match => match.Groups["num"].Value, RegexOptions.CultureInvariant);

    private static string RemoveYearFromPublicationReference(string? value)
        => PublicationNumberWithoutYear(value);

    private static string FirstValue(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            var pair = values.FirstOrDefault(x => NormalizePlain(x.Key).Equals(NormalizePlain(key), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pair.Value)) return pair.Value.Trim();
        }
        return string.Empty;
    }

    private static string NormalizePlain(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : ' ');
        }
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static string ReplaceKey(string text, string key, string value)
    {
        var escaped = Regex.Escape(key.Trim());
        return Regex.Replace(text, $@"\{{\{{\s*{escaped}(?::[^}}]+)?\s*\}}\}}|\{{\s*{escaped}(?::[^}}]+)?\s*\}}", _ => value ?? string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public async Task ExportReportAsync(string path, int year, CancellationToken cancellationToken = default)
    {
        var periods = await GetPeriodsAsync(year, cancellationToken);
        var all = await GetAllocationsAsync(year, null, cancellationToken);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? OutputDirectory);
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, "[Content_Types].xml", XlsxContentTypes, cancellationToken);
                await WriteEntryAsync(archive, "_rels/.rels", XlsxRootRels, cancellationToken);
                await WriteEntryAsync(archive, "xl/workbook.xml", XlsxWorkbook(year), cancellationToken);
                await WriteEntryAsync(archive, "xl/_rels/workbook.xml.rels", XlsxWorkbookRels, cancellationToken);
                await WriteEntryAsync(archive, "xl/styles.xml", XlsxStyles, cancellationToken);
                await WriteEntryAsync(archive, "xl/worksheets/sheet1.xml", BuildVacationSheet(periods, all), cancellationToken);
            }
            ValidateWorkbook(temp);
            File.Move(temp, path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    public async Task ExportPreviewWordAsync(string path, string title, string text, IReadOnlyList<MilitaryRecord> military, CancellationToken cancellationToken = default)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? OutputDirectory);
        var document = BuildWordDocument(title, text, military);
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(zip, "[Content_Types].xml", WordContentTypes, cancellationToken);
                await WriteEntryAsync(zip, "_rels/.rels", WordRootRels, cancellationToken);
                await WriteEntryAsync(zip, "word/document.xml", document, cancellationToken);
            }
            using (var validate = ZipFile.OpenRead(temp))
                if (validate.GetEntry("word/document.xml") is null) throw new InvalidDataException("Documento Word incompleto.");
            File.Move(temp, path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static string BuildWordDocument(string title, string text, IReadOnlyList<MilitaryRecord> military)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        AppendWordParagraph(sb, title, true, military);
        foreach (var line in (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) AppendWordParagraph(sb, line, false, military);
        sb.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\"/></w:sectPr></w:body></w:document>");
        return sb.ToString();
    }

    private static void AppendWordParagraph(StringBuilder sb, string line, bool title, IReadOnlyList<MilitaryRecord> military)
    {
        sb.Append("<w:p><w:pPr><w:spacing w:after=\"0\"/><w:jc w:val=\"").Append(title ? "center" : "both").Append("\"/></w:pPr>");
        var segments = BulletinTextFormatter.FindWarNameRanges(line, military);
        var pos = 0;
        foreach (var segment in segments.OrderBy(x => x.Start))
        {
            if (segment.Start < pos) continue;
            AppendWordRun(sb, line[pos..segment.Start], false);
            AppendWordRun(sb, line.Substring(segment.Start, Math.Min(segment.Length, line.Length - segment.Start)), true);
            pos = segment.Start + segment.Length;
        }
        if (pos < line.Length) AppendWordRun(sb, line[pos..], false);
        if (line.Length == 0) AppendWordRun(sb, string.Empty, false);
        sb.Append("</w:p>");
    }

    private static void AppendWordRun(StringBuilder sb, string text, bool bold)
    {
        sb.Append("<w:r><w:rPr><w:rFonts w:ascii=\"Times New Roman\" w:hAnsi=\"Times New Roman\"/><w:sz w:val=\"20\"/>");
        sb.Append(bold ? "<w:b/>" : "<w:b w:val=\"0\"/>");
        sb.Append("</w:rPr><w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(text) ?? string.Empty).Append("</w:t></w:r>");
    }

    private static string BuildVacationSheet(IReadOnlyList<VacationPeriod> periods, IReadOnlyList<VacationAllocation> allocations)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"/></sheetViews><cols><col min=\"1\" max=\"1\" width=\"15\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"52\" customWidth=\"1\"/><col min=\"3\" max=\"3\" width=\"10\" customWidth=\"1\"/><col min=\"4\" max=\"4\" width=\"12\" customWidth=\"1\"/></cols><sheetData>");
        var row = 1;
        foreach (var period in periods)
        {
            sb.Append($"<row r=\"{row}\"><c r=\"A{row}\" s=\"1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(period.FullLabel)}</t></is></c></row>");
            row++;
            sb.Append($"<row r=\"{row}\">");
            foreach (var pair in new[] { ("A", "P/G"), ("B", "NOME COMPLETO"), ("C", "DIAS"), ("D", "PAGO") })
                sb.Append($"<c r=\"{pair.Item1}{row}\" s=\"2\" t=\"inlineStr\"><is><t>{pair.Item2}</t></is></c>");
            sb.Append("</row>"); row++;
            var rows = allocations.Where(x => x.PeriodId == period.Id).OrderBy(x => MilitaryRankService.GetOrder(x.Military.Rank)).ThenBy(x => x.Military.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (rows.Count == 0)
            {
                sb.Append($"<row r=\"{row}\"><c r=\"A{row}\" s=\"5\" t=\"inlineStr\"><is><t>(sem militares alocados neste período)</t></is></c></row>");
                row += 2; continue;
            }
            foreach (var allocation in rows)
            {
                var style = row % 2 == 0 ? 3 : 4;
                sb.Append($"<row r=\"{row}\">");
                XCell(sb, $"A{row}", allocation.Military.ShortRank, style);
                XRichName(sb, $"B{row}", allocation.Military, style);
                XCell(sb, $"C{row}", allocation.Days.ToString(CultureInfo.InvariantCulture), style);
                XCell(sb, $"D{row}", allocation.IsPaid ? "✔" : string.Empty, style);
                sb.Append("</row>"); row++;
            }
            row++;
        }
        sb.Append("</sheetData><mergeCells count=\"").Append(periods.Count).Append("\">");
        var mergeRow = 1;
        foreach (var period in periods)
        {
            sb.Append($"<mergeCell ref=\"A{mergeRow}:D{mergeRow}\"/>");
            var count = allocations.Count(x => x.PeriodId == period.Id);
            mergeRow += count == 0 ? 4 : count + 3;
        }
        sb.Append("</mergeCells><pageMargins left=\"0.25\" right=\"0.25\" top=\"0.4\" bottom=\"0.4\" header=\"0.2\" footer=\"0.2\"/><pageSetup orientation=\"landscape\" fitToWidth=\"1\" fitToHeight=\"0\"/></worksheet>");
        return sb.ToString();
    }

    private static void XCell(StringBuilder sb, string reference, string value, int style)
        => sb.Append($"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(value) ?? string.Empty}</t></is></c>");

    private static void XRichName(StringBuilder sb, string reference, MilitaryRecord military, int style)
    {
        sb.Append($"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is>");
        foreach (var segment in NameHighlightHelper.BuildSegments(military.Name.ToUpperInvariant(), military.WarName.ToUpperInvariant()))
        {
            sb.Append("<r><rPr><rFont val=\"Calibri\"/><sz val=\"11\"/>");
            if (segment.IsBold) sb.Append("<b/>");
            sb.Append("</rPr><t xml:space=\"preserve\">").Append(SecurityElement.Escape(segment.Text) ?? string.Empty).Append("</t></r>");
        }
        sb.Append("</is></c>");
    }

    private static void ValidateWorkbook(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (var required in new[] { "[Content_Types].xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml", "xl/styles.xml" })
            if (archive.GetEntry(required) is null) throw new InvalidDataException("Planilha incompleta: " + required);
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string text, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private static Dictionary<string, string> DefaultModels() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADICIONAL FERIAS - Ordem de Saque"] =
            "Com fundamento na alínea “d” do inciso II do art. 2º da Medida Provisória nº 2.215-10, de 31 AGO 01, no Decreto nº 4.307, de 18 JUL 02, e observadas as normas vigentes do Plano de Férias do Exército, seja realizado o saque do adicional de férias, relativo ao ano de referência {ANO_REF}, em favor do(s) militar(es) abaixo relacionado(s), por estar(em) previsto(s) para gozar férias no {PERIODO} ({DATA_INICIO_ABREV} a {DATA_FIM_ABREV}), conforme publicado no {{BI_REFERENCIA}}.\n\n" +
            "{LISTA}",
        ["ADICIONAL FERIAS - Saque de Atrasados"] =
            "Com fundamento na alínea “d” do inciso II do art. 2º da Medida Provisória nº 2.215-10, de 31 AGO 01, no Decreto nº 4.307, de 18 JUL 02, e observadas as normas vigentes do Plano de Férias do Exército, seja realizado o saque de atrasados do adicional de férias, relativo ao ano de referência {ANO_REF}, {TEXTO_VALOR_ATRASADO}, em favor do(s) militar(es) abaixo relacionado(s), tendo em vista que gozou(aram) férias no {PERIODO} ({DATA_INICIO_ABREV} a {DATA_FIM_ABREV}) e não recebeu(ram) o direito remuneratório na competência própria, conforme publicado no {{BI_REFERENCIA}}.\n\n" +
            "Para fins de conferência e lançamento no SIPPES, cada militar segue com valor próprio calculado na Carteira de Férias, sem utilização de valor único para o grupo, constando período, ano de referência, quantidade de meses/dias e valor solicitado.\n\n" +
            "{LISTA}",
        ["ADICIONAL FERIAS - Despesa a Anular"] =
            "Com fundamento na Medida Provisória nº 2.215-10, de 31 AGO 01, no Decreto nº 4.307, de 18 JUL 02, e nas normas administrativas de pagamento aplicáveis, seja realizada a despesa a anular referente ao adicional de férias do(s) militar(es) abaixo relacionado(s), relativo ao ano de referência {ANO_REF}, em razão de recebimento indevido ou a maior.\n\n" +
            "Motivo específico: {{MOTIVO_DESPESA_ANULAR}}\nPeríodo de férias: {PERIODO} ({DATA_INICIO_ABREV} a {DATA_FIM_ABREV})\nQuantidade de dias: {DIAS}\nValor a anular: {{VALOR_ANULAR:VALOR=AMBOS}}\n\n" +
            "{LISTA}",
        ["ANTECIPAÇÃO 1ª PARCELA ADICIONAL NATALINO - Férias"] =
            "Seja sacada a antecipação da primeira parcela do adicional natalino por motivo de férias, ano de referência {{ANO_NATALINO}}, em favor do(s) militar(es) abaixo relacionado(s), " +
            "em virtude de gozo de férias no {PERIODO} ({DATA_INICIO} a {DATA_FIM}), totalizando {DIAS} dia(s), conforme publicado no {{BI_REFERENCIA}}.\n\n" +
            "{LISTA}",
        ["ANTECIPAÇÃO 1ª PARCELA ADICIONAL NATALINO - Férias Atrasadas"] =
            "Seja sacada a antecipação da primeira parcela do adicional natalino por motivo de férias atrasadas, ano de referência {{ANO_NATALINO}}, em favor do(s) militar(es) abaixo relacionado(s), " +
            "tendo em vista que as férias iniciaram no mês de pagamento em aberto ou já foram gozadas, conforme período {PERIODO} ({DATA_INICIO} a {DATA_FIM}), totalizando {DIAS} dia(s).\n\n" +
            "{LISTA}",
        ["INDENIZAÇÃO DE FÉRIAS - Ordem de Saque"] =
            "Seja sacada a indenização de férias do(s) militar(es) abaixo relacionado(s), relativo ao ano de referência {ANO_REF}, na quantidade de {{QTD_MESES}} mês(es), " +
            "em razão de desligamento/licenciamento sem gozo do período aquisitivo correspondente.\n\n" +
            "Data de desligamento: {{DATA_DESLIGAMENTO:DATA=ABREV}}\nMotivo: {{MOTIVO_DESLIGAMENTO}}\n\n{LISTA}",
        ["ADICIONAL DE FÉRIAS - Reserva Remunerada"] =
            "Seja sacado o adicional de férias de militar transferido para a reserva remunerada/desligado do serviço ativo, referente ao ano de {ANO_REF}, " +
            "na quantidade de {{QTD_MESES}} mês(es), em favor do(s) militar(es) abaixo relacionado(s).\n\n" +
            "{LISTA}",
        ["INDENIZAÇÃO DE FÉRIAS - Reserva Remunerada"] =
            "Seja sacada a indenização de férias de militar transferido para a reserva remunerada/desligado do serviço ativo, referente ao ano de {ANO_REF}, " +
            "na quantidade de {{QTD_MESES}} mês(es), em favor do(s) militar(es) abaixo relacionado(s).\n\n" +
            "{LISTA}",
        ["AUXÍLIO-ALIMENTAÇÃO - Ordem de Saque (Cb e Sd Férias)"] =
            "Seja realizado o saque do auxílio-alimentação referente a {DIAS} dia(s) de férias, em favor exclusivamente do(s) Cabo(s) e Soldado(s) abaixo nominado(s), em virtude de estar(em) previsto(s) para gozar férias no {PERIODO} ({DATA_INICIO} a {DATA_FIM}), férias relativas ao ano de {ANO_REF}, conforme publicado no {{BI_REFERENCIA}}, e de acordo com o previsto no caput do art. 69 do Decreto nº 4.307, de 18 JUL 02.\n\n" +
            "{LISTA}"
        ,
        ["AUXÍLIO-ALIMENTAÇÃO - Saque de Atrasados (Cb e Sd Férias)"] =
            "Seja realizado o saque de atrasado do auxílio-alimentação referente a {DIAS} dia(s) de férias já gozadas e não pagas na competência devida, em favor exclusivamente do(s) Cabo(s) e Soldado(s) abaixo nominado(s), no {PERIODO} ({DATA_INICIO} a {DATA_FIM}), férias relativas ao ano de {ANO_REF}, conforme publicado no {{BI_REFERENCIA}}.\n\n" +
            "Código: A48 - Aux Alim atrasado 1X. O valor individual é calculado por R$ 13,50 x quantidade de dias.\n\n" +
            "{LISTA}"
    };

    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    private static string FormatCpf(string? value)
    {
        var digits = Digits(value);
        return digits.Length == 11 ? $"{digits[..3]}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits[9..]}" : digits;
    }

    private static string XlsxWorkbook(int year) => $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Plano {year}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
    private const string XlsxContentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>";
    private const string XlsxRootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
    private const string XlsxWorkbookRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
    private const string XlsxStyles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"12\"/><name val=\"Calibri\"/></font><font><b/><color rgb=\"FFFFFFFF\"/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"4\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E79\"/></patternFill></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF3F6FB\"/></patternFill></fill></fills><borders count=\"2\"><border/><border><left style=\"thin\"><color rgb=\"FFD0D7DE\"/></left><right style=\"thin\"><color rgb=\"FFD0D7DE\"/></right><top style=\"thin\"><color rgb=\"FFD0D7DE\"/></top><bottom style=\"thin\"><color rgb=\"FFD0D7DE\"/></bottom></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"6\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\"/></xf><xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"3\" borderId=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" applyAlignment=\"1\"><alignment vertical=\"center\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
    private const string WordContentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>";
    private const string WordRootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
}
