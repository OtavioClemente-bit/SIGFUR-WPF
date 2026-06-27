using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class MilitaryRepository
{
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public MilitaryRepository(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public string DatabasePath => _paths.DatabaseFile;

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 12
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=12000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            Directory.CreateDirectory(_paths.DataDirectory);
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS militares(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    posto TEXT NOT NULL DEFAULT '',
                    nome TEXT NOT NULL DEFAULT '',
                    nome_guerra TEXT NOT NULL DEFAULT '',
                    cpf TEXT UNIQUE NOT NULL DEFAULT '',
                    prec_cp TEXT UNIQUE NOT NULL DEFAULT '',
                    idt TEXT UNIQUE NOT NULL DEFAULT '',
                    banco TEXT,
                    agencia TEXT,
                    conta TEXT,
                    foto TEXT,
                    ano TEXT,
                    data_nascimento TEXT,
                    data_praca TEXT,
                    endereco TEXT,
                    cep TEXT,
                    recebe_pre_escolar TEXT DEFAULT 'Não',
                    valor_pre_escolar TEXT DEFAULT '0.00',
                    recebe_aux_transporte TEXT DEFAULT 'Não',
                    valor_aux_transporte TEXT DEFAULT '0.00',
                    pnr TEXT DEFAULT 'Não',
                    pensao_alimenticia TEXT DEFAULT 'Não',
                    valor_pensao TEXT,
                    aux_total_bruto REAL,
                    aux_dias_uteis INTEGER,
                    aux_base_ts TEXT
                );

                CREATE TABLE IF NOT EXISTS militares_contato(
                    militar_id INTEGER PRIMARY KEY,
                    cpf TEXT,
                    telefone TEXT,
                    email TEXT,
                    atualizado_em TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_militares_contato_cpf ON militares_contato(cpf);

                CREATE TABLE IF NOT EXISTS recrutas_extra(
                    cpf TEXT PRIMARY KEY,
                    telefone TEXT,
                    email TEXT,
                    escolaridade TEXT,
                    criado_em TEXT
                );

                CREATE TABLE IF NOT EXISTS militar_documentos(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    militar_id INTEGER NOT NULL,
                    tipo TEXT NOT NULL,
                    titulo TEXT,
                    caminho TEXT NOT NULL,
                    nome_arquivo TEXT,
                    data_salvo TEXT NOT NULL,
                    data_salvo_br TEXT NOT NULL,
                    origem_pdf TEXT,
                    observacao TEXT,
                    chaves_json TEXT,
                    criado_em TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_militar_documentos_militar ON militar_documentos(militar_id);

                CREATE TABLE IF NOT EXISTS aux_transporte_tarifas(
                    militar_id INTEGER NOT NULL,
                    idx INTEGER NOT NULL,
                    tarifa REAL NOT NULL DEFAULT 0,
                    PRIMARY KEY (militar_id, idx)
                );

                CREATE TABLE IF NOT EXISTS soldos_por_posto(
                    posto TEXT PRIMARY KEY,
                    soldo REAL NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS tempo_servico_intervalos(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    militar_id INTEGER NOT NULL,
                    data_inicio TEXT NOT NULL,
                    data_fim TEXT,
                    observacao TEXT,
                    ordem INTEGER NOT NULL DEFAULT 0,
                    ativo INTEGER NOT NULL DEFAULT 1,
                    criado_em TEXT,
                    atualizado_em TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_tempo_servico_militar ON tempo_servico_intervalos(militar_id, ativo, ordem, id);

                CREATE TABLE IF NOT EXISTS lt_militares(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    posto TEXT,
                    nome TEXT NOT NULL,
                    nome_guerra TEXT,
                    cpf TEXT,
                    prec_cp TEXT,
                    idt TEXT,
                    banco TEXT,
                    agencia TEXT,
                    conta TEXT,
                    foto TEXT,
                    ano TEXT,
                    data_nascimento TEXT,
                    data_praca TEXT,
                    endereco TEXT,
                    cep TEXT,
                    recebe_pre_escolar TEXT,
                    valor_pre_escolar TEXT,
                    recebe_aux_transporte TEXT,
                    valor_aux_transporte TEXT,
                    pnr TEXT,
                    motivo TEXT,
                    destino TEXT,
                    visivel INTEGER DEFAULT 1
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureOptionalColumnsAsync(connection, "militares", new Dictionary<string, string>
            {
                ["pensao_alimenticia"] = "TEXT DEFAULT 'Não'",
                ["valor_pensao"] = "TEXT",
                ["aux_total_bruto"] = "REAL",
                ["aux_dias_uteis"] = "INTEGER",
                ["aux_base_ts"] = "TEXT",
                ["adido_encostado"] = "INTEGER NOT NULL DEFAULT 0"
            }, cancellationToken);
            await EnsureOptionalColumnsAsync(connection, "militar_documentos", new Dictionary<string, string>
            {
                ["chaves_json"] = "TEXT"
            }, cancellationToken);
            await EnsureOptionalColumnsAsync(connection, "aux_transporte_tarifas", new Dictionary<string, string>
            {
                ["linha"] = "TEXT NOT NULL DEFAULT ''",
                ["nome"] = "TEXT NOT NULL DEFAULT ''",
                ["categoria"] = "TEXT NOT NULL DEFAULT ''",
                ["url"] = "TEXT NOT NULL DEFAULT ''"
            }, cancellationToken);
            await EnsureOptionalColumnsAsync(connection, "lt_militares", new Dictionary<string, string>
            {
                ["telefone"] = "TEXT",
                ["email"] = "TEXT",
                ["escolaridade"] = "TEXT",
                ["pensao_alimenticia"] = "TEXT DEFAULT 'Não'",
                ["valor_pensao"] = "TEXT",
                ["aux_total_bruto"] = "REAL",
                ["aux_dias_uteis"] = "INTEGER",
                ["aux_base_ts"] = "TEXT",
                ["adido_encostado"] = "INTEGER NOT NULL DEFAULT 0"
            }, cancellationToken);
            await NormalizeStoredRanksAsync(connection, cancellationToken);
            await NormalizeStoredDatesAsync(connection, cancellationToken);
            await NormalizeStoredNamesAsync(connection, cancellationToken);
            _schemaReady = true;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao preparar estrutura nativa de Listar Militares.", ex);
            throw;
        }
        finally { _schemaGate.Release(); }
    }

    private static async Task NormalizeStoredRanksAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Corrige apenas grafias/abreviações reconhecíveis. A regra exige a palavra
        // Tenente ou Sargento, então "1º Sgt" jamais é convertido em "1º Ten".
        foreach (var table in new[] { "militares", "lt_militares" })
        {
            var columns = await GetTableColumnsAsync(connection, table, cancellationToken);
            if (!columns.Contains("id") || !columns.Contains("posto")) continue;
            var changes = new List<(int Id, string Rank)>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText = $"SELECT id, posto FROM {table} WHERE TRIM(COALESCE(posto,''))<>'';";
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetInt32(0);
                    var current = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1)) ?? string.Empty;
                    var canonical = MilitaryRankService.Canonicalize(current);
                    if (!string.IsNullOrWhiteSpace(canonical) && !canonical.Equals(current.Trim(), StringComparison.Ordinal))
                        changes.Add((id, canonical));
                }
            }

            foreach (var (id, rank) in changes)
            {
                await using var update = connection.CreateCommand();
                update.CommandText = $"UPDATE {table} SET posto=$posto WHERE id=$id;";
                update.Parameters.AddWithValue("$posto", rank);
                update.Parameters.AddWithValue("$id", id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task NormalizeStoredDatesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "militares", "lt_militares" })
        {
            var columns = await GetTableColumnsAsync(connection, table, cancellationToken);
            if (!columns.Contains("id") || !columns.Contains("data_nascimento") || !columns.Contains("data_praca")) continue;
            var changes = new List<(int Id, string BirthDate, string EnlistmentDate)>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText = $"SELECT id, data_nascimento, data_praca FROM {table};";
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetInt32(0);
                    var birth = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1)) ?? string.Empty;
                    var enlistment = reader.IsDBNull(2) ? string.Empty : Convert.ToString(reader.GetValue(2)) ?? string.Empty;
                    var normalizedBirth = MilitaryFormatting.NormalizeDateText(birth);
                    var normalizedEnlistment = MilitaryFormatting.NormalizeDateText(enlistment);
                    if (!normalizedBirth.Equals(birth.Trim(), StringComparison.Ordinal)
                        || !normalizedEnlistment.Equals(enlistment.Trim(), StringComparison.Ordinal))
                        changes.Add((id, normalizedBirth, normalizedEnlistment));
                }
            }

            foreach (var item in changes)
            {
                await using var update = connection.CreateCommand();
                update.CommandText = $"UPDATE {table} SET data_nascimento=$nascimento, data_praca=$praca WHERE id=$id;";
                update.Parameters.AddWithValue("$nascimento", item.BirthDate);
                update.Parameters.AddWithValue("$praca", item.EnlistmentDate);
                update.Parameters.AddWithValue("$id", item.Id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var intervalColumns = await GetTableColumnsAsync(connection, "tempo_servico_intervalos", cancellationToken);
        if (!intervalColumns.Contains("id") || !intervalColumns.Contains("data_inicio") || !intervalColumns.Contains("data_fim")) return;

        var intervalChanges = new List<(int Id, string StartDate, string EndDate)>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT id, data_inicio, data_fim FROM tempo_servico_intervalos;";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                var start = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1)) ?? string.Empty;
                var end = reader.IsDBNull(2) ? string.Empty : Convert.ToString(reader.GetValue(2)) ?? string.Empty;
                var normalizedStart = MilitaryFormatting.NormalizeDateText(start);
                var normalizedEnd = string.IsNullOrWhiteSpace(end) ? string.Empty : MilitaryFormatting.NormalizeDateText(end);
                if (!normalizedStart.Equals(start.Trim(), StringComparison.Ordinal)
                    || !normalizedEnd.Equals(end.Trim(), StringComparison.Ordinal))
                    intervalChanges.Add((id, normalizedStart, normalizedEnd));
            }
        }
        foreach (var item in intervalChanges)
        {
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE tempo_servico_intervalos SET data_inicio=$inicio, data_fim=$fim WHERE id=$id;";
            update.Parameters.AddWithValue("$inicio", item.StartDate);
            update.Parameters.AddWithValue("$fim", item.EndDate);
            update.Parameters.AddWithValue("$id", item.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }


    private static async Task NormalizeStoredNamesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "militares", "lt_militares" })
        {
            var columns = await GetTableColumnsAsync(connection, table, cancellationToken);
            if (!columns.Contains("id") || !columns.Contains("nome")) continue;
            var warColumn = columns.Contains("nome_guerra");
            var changes = new List<(int Id, string Name, string WarName)>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText = warColumn
                    ? $"SELECT id,nome,nome_guerra FROM {table};"
                    : $"SELECT id,nome,'' FROM {table};";
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetInt32(0);
                    var currentName = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1)) ?? string.Empty;
                    var currentWar = reader.IsDBNull(2) ? string.Empty : Convert.ToString(reader.GetValue(2)) ?? string.Empty;
                    var name = UpperName(currentName);
                    var war = UpperName(currentWar);
                    if (!name.Equals(currentName.Trim(), StringComparison.Ordinal) || !war.Equals(currentWar.Trim(), StringComparison.Ordinal))
                        changes.Add((id, name, war));
                }
            }
            foreach (var item in changes)
            {
                await using var update = connection.CreateCommand();
                update.CommandText = warColumn
                    ? $"UPDATE {table} SET nome=$nome,nome_guerra=$guerra WHERE id=$id;"
                    : $"UPDATE {table} SET nome=$nome WHERE id=$id;";
                update.Parameters.AddWithValue("$nome", item.Name);
                if (warColumn) update.Parameters.AddWithValue("$guerra", item.WarName);
                update.Parameters.AddWithValue("$id", item.Id);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static string UpperName(string? value)
        => Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ").ToUpper(CultureInfo.GetCultureInfo("pt-BR"));

    private static async Task<HashSet<string>> GetTableColumnsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task EnsureOptionalColumnsAsync(SqliteConnection connection, string table, IReadOnlyDictionary<string, string> columns, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(1));
        }
        foreach (var (name, ddl) in columns)
        {
            if (existing.Contains(name)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {ddl};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<List<MilitaryRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var list = new List<MilitaryRecord>();
        await using var connection = OpenConnection();
        if (!await TableExistsAsync(connection, "militares", cancellationToken)) return list;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.*,
                   COALESCE(NULLIF(c.telefone,''), NULLIF(r.telefone,''), '') AS contato_telefone,
                   COALESCE(NULLIF(c.email,''), NULLIF(r.email,''), '') AS contato_email,
                   COALESCE(NULLIF(r.escolaridade,''), '') AS contato_escolaridade
              FROM militares m
              LEFT JOIN militares_contato c ON c.militar_id = m.id
              LEFT JOIN recrutas_extra r ON REPLACE(REPLACE(REPLACE(r.cpf,'.',''),'-',''),' ','') = REPLACE(REPLACE(REPLACE(m.cpf,'.',''),'-',''),' ','')
             ORDER BY m.nome COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) list.Add(ReadMilitary(reader));
        return list;
    }

    public async Task<MilitaryRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.*,
                   COALESCE(NULLIF(c.telefone,''), NULLIF(r.telefone,''), '') AS contato_telefone,
                   COALESCE(NULLIF(c.email,''), NULLIF(r.email,''), '') AS contato_email,
                   COALESCE(NULLIF(r.escolaridade,''), '') AS contato_escolaridade
              FROM militares m
              LEFT JOIN militares_contato c ON c.militar_id = m.id
              LEFT JOIN recrutas_extra r ON REPLACE(REPLACE(REPLACE(r.cpf,'.',''),'-',''),' ','') = REPLACE(REPLACE(REPLACE(m.cpf,'.',''),'-',''),' ','')
             WHERE m.id=$id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMilitary(reader) : null;
    }

    public async Task<IReadOnlyList<string>> GetRanksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        var list = new List<string>();
        if (await TableExistsAsync(connection, "postos", cancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT nome FROM postos WHERE TRIM(COALESCE(nome,''))<>'' ORDER BY id;";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var rank = MilitaryRankService.Canonicalize(reader.GetString(0));
                if (!string.IsNullOrWhiteSpace(rank)) list.Add(rank);
            }
        }
        if (list.Count == 0)
        {
            list.AddRange((await GetAllAsync(cancellationToken)).Select(x => x.Rank).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(RankOrder).ThenBy(x => x).ToList();
    }

    public async Task<IReadOnlyList<string>> GetBanksAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        var list = new List<string>();
        if (await TableExistsAsync(connection, "bancos", cancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT nome FROM bancos WHERE TRIM(COALESCE(nome,''))<>'' ORDER BY id;";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) list.Add(reader.GetString(0));
        }
        return list;
    }

    public async Task<int> SaveAsync(MilitaryRecord military, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(military);
        await EnsureSchemaAsync(cancellationToken);
        ValidateMilitary(military);
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ValidateUniqueAsync(connection, (SqliteTransaction)transaction, military, cancellationToken);
            military.Rank = MilitaryRankService.Canonicalize(military.Rank);
            var values = new Dictionary<string, object?>
            {
                ["posto"] = military.Rank, ["nome"] = UpperName(military.Name), ["nome_guerra"] = UpperName(military.WarName),
                ["cpf"] = MilitaryFormatting.Digits(military.Cpf), ["prec_cp"] = military.PrecCp.Trim(), ["idt"] = military.MilitaryId.Trim(),
                ["banco"] = military.Bank.Trim(), ["agencia"] = military.Agency.Trim(), ["conta"] = military.Account.Trim(),
                ["foto"] = military.PhotoPath.Trim(), ["ano"] = military.FormationYear.Trim(), ["data_nascimento"] = MilitaryFormatting.NormalizeDateText(military.BirthDate),
                ["data_praca"] = MilitaryFormatting.NormalizeDateText(military.EnlistmentDate), ["endereco"] = military.Address.Trim(), ["cep"] = MilitaryFormatting.Digits(military.ZipCode),
                ["recebe_pre_escolar"] = NormalizeYesNo(military.ReceivesPreSchool), ["valor_pre_escolar"] = military.PreSchoolValue.Trim(),
                ["recebe_aux_transporte"] = military.IsAttached ? "Não" : NormalizeYesNo(military.ReceivesTransportAid),
                ["valor_aux_transporte"] = military.IsAttached ? "0.00" : military.TransportAidValue.Trim(), ["pnr"] = NormalizeYesNo(military.HasPnr),
                ["pensao_alimenticia"] = NormalizeYesNo(military.Alimony), ["valor_pensao"] = military.AlimonyValue.Trim(),
                ["aux_total_bruto"] = military.IsAttached ? 0d : military.TransportGrossTotal, ["aux_dias_uteis"] = military.TransportWorkingDays,
                ["aux_base_ts"] = military.TransportBaseTimestamp,
                ["adido_encostado"] = military.IsAttached ? 1 : 0
            };
            var available = await GetColumnsAsync(connection, "militares", cancellationToken, (SqliteTransaction)transaction);
            values = values.Where(x => available.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);

            if (military.Id <= 0)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = $"INSERT INTO militares ({string.Join(',', values.Keys)}) VALUES ({string.Join(',', values.Keys.Select(k => '$' + k))}); SELECT last_insert_rowid();";
                foreach (var (key, value) in values) insert.Parameters.AddWithValue('$' + key, value ?? DBNull.Value);
                military.Id = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                await using var update = connection.CreateCommand();
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = $"UPDATE militares SET {string.Join(',', values.Keys.Select(k => k + "=$" + k))} WHERE id=$id;";
                foreach (var (key, value) in values) update.Parameters.AddWithValue('$' + key, value ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", military.Id);
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) throw new InvalidOperationException("Militar não encontrado para atualização.");
            }

            await UpsertContactAsync(connection, (SqliteTransaction)transaction, military, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return military.Id;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        // Igual ao módulo original: remove apenas a linha ativa. Dados vinculados
        // permanecem disponíveis para restauração da lixeira com o mesmo ID.
        command.CommandText = "DELETE FROM militares WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RestoreAsync(MilitaryRecord military, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(military);
        if (military.Id <= 0) throw new InvalidOperationException("O registro da lixeira não possui um ID válido.");
        await EnsureSchemaAsync(cancellationToken);
        ValidateMilitary(military);
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var occupied = connection.CreateCommand())
            {
                occupied.Transaction = (SqliteTransaction)transaction;
                occupied.CommandText = "SELECT 1 FROM militares WHERE id=$id LIMIT 1;";
                occupied.Parameters.AddWithValue("$id", military.Id);
                if (await occupied.ExecuteScalarAsync(cancellationToken) is not null)
                    throw new InvalidOperationException($"O ID {military.Id} já está em uso na lista ativa.");
            }

            await ValidateUniqueAsync(connection, (SqliteTransaction)transaction, military, cancellationToken);
            military.Rank = MilitaryRankService.Canonicalize(military.Rank);
            var values = new Dictionary<string, object?>
            {
                ["id"] = military.Id,
                ["posto"] = military.Rank, ["nome"] = UpperName(military.Name), ["nome_guerra"] = UpperName(military.WarName),
                ["cpf"] = MilitaryFormatting.Digits(military.Cpf), ["prec_cp"] = military.PrecCp.Trim(), ["idt"] = military.MilitaryId.Trim(),
                ["banco"] = military.Bank.Trim(), ["agencia"] = military.Agency.Trim(), ["conta"] = military.Account.Trim(),
                ["foto"] = military.PhotoPath.Trim(), ["ano"] = military.FormationYear.Trim(), ["data_nascimento"] = MilitaryFormatting.NormalizeDateText(military.BirthDate),
                ["data_praca"] = MilitaryFormatting.NormalizeDateText(military.EnlistmentDate), ["endereco"] = military.Address.Trim(), ["cep"] = MilitaryFormatting.Digits(military.ZipCode),
                ["recebe_pre_escolar"] = NormalizeYesNo(military.ReceivesPreSchool), ["valor_pre_escolar"] = military.PreSchoolValue.Trim(),
                ["recebe_aux_transporte"] = NormalizeYesNo(military.ReceivesTransportAid), ["valor_aux_transporte"] = military.TransportAidValue.Trim(),
                ["pnr"] = NormalizeYesNo(military.HasPnr), ["pensao_alimenticia"] = NormalizeYesNo(military.Alimony),
                ["valor_pensao"] = military.AlimonyValue.Trim(), ["aux_total_bruto"] = military.TransportGrossTotal,
                ["aux_dias_uteis"] = military.TransportWorkingDays, ["aux_base_ts"] = military.TransportBaseTimestamp,
                ["adido_encostado"] = military.IsAttached ? 1 : 0
            };
            var available = await GetColumnsAsync(connection, "militares", cancellationToken, (SqliteTransaction)transaction);
            values = values.Where(x => available.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = $"INSERT INTO militares ({string.Join(',', values.Keys)}) VALUES ({string.Join(',', values.Keys.Select(k => '$' + k))});";
            foreach (var (key, value) in values) insert.Parameters.AddWithValue('$' + key, value ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> TransferToLicensedAsync(MilitaryRecord military, string reason, string destination, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var ltId = await NextLicensedNegativeIdAsync(connection, (SqliteTransaction)transaction, cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO lt_militares(
                    id,posto,nome,nome_guerra,cpf,prec_cp,idt,banco,agencia,conta,foto,ano,data_nascimento,data_praca,endereco,cep,
                    recebe_pre_escolar,valor_pre_escolar,recebe_aux_transporte,valor_aux_transporte,pnr,motivo,destino,visivel,
                    telefone,email,escolaridade,pensao_alimenticia,valor_pensao,aux_total_bruto,aux_dias_uteis,aux_base_ts,adido_encostado)
                VALUES($ltId,$posto,$nome,$ng,$cpf,$prec,$idt,$banco,$agencia,$conta,$foto,$ano,$nascimento,$praca,$endereco,$cep,
                       $rpe,$vpe,$rat,$vat,$pnr,$motivo,$destino,1,$telefone,$email,$escolaridade,$pensao,$valorPensao,$auxBruto,$auxDias,$auxTs,$adido);
                """;
            insert.Parameters.AddWithValue("$ltId", ltId);
            AddMilitaryParameters(insert, military);
            insert.Parameters.AddWithValue("$motivo", reason?.Trim() ?? string.Empty);
            insert.Parameters.AddWithValue("$destino", destination?.Trim() ?? string.Empty);
            insert.Parameters.AddWithValue("$telefone", military.Phone ?? string.Empty);
            insert.Parameters.AddWithValue("$email", military.Email ?? string.Empty);
            insert.Parameters.AddWithValue("$escolaridade", military.Education ?? string.Empty);
            insert.Parameters.AddWithValue("$pensao", military.Alimony ?? "Não");
            insert.Parameters.AddWithValue("$valorPensao", military.AlimonyValue ?? string.Empty);
            insert.Parameters.AddWithValue("$auxBruto", military.TransportGrossTotal is null ? DBNull.Value : military.TransportGrossTotal.Value);
            insert.Parameters.AddWithValue("$auxDias", military.TransportWorkingDays is null ? DBNull.Value : military.TransportWorkingDays.Value);
            insert.Parameters.AddWithValue("$auxTs", military.TransportBaseTimestamp ?? string.Empty);
            insert.Parameters.AddWithValue("$adido", military.IsAttached ? 1 : 0);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            var newId = ltId;

            // As tabelas de documentos/contracheques/AT usam o ID numérico do militar.
            // Quando o novo ID da LT coincide com o ID do cadastro principal, os vínculos
            // já estão corretos e NÃO podem ser copiados/apagados (isso zerava o AT e
            // duplicava documentos). Só migra os vínculos quando os IDs forem diferentes.
            if (newId != military.Id)
                await CopyLinkedDataAsync(connection, (SqliteTransaction)transaction, military.Id, newId, military, cancellationToken);

            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = newId == military.Id
                ? "DELETE FROM militares WHERE id=$id;"
                : "DELETE FROM militares WHERE id=$id; DELETE FROM militares_contato WHERE militar_id=$id; DELETE FROM aux_transporte_tarifas WHERE militar_id=$id; DELETE FROM tempo_servico_intervalos WHERE militar_id=$id; DELETE FROM militar_documentos WHERE militar_id=$id;";
            delete.Parameters.AddWithValue("$id", military.Id);
            await delete.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return newId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task PromoteAsync(IEnumerable<int> ids, string rank, CancellationToken cancellationToken = default)
    {
        var idList = ids.Distinct().Where(x => x > 0).ToList();
        if (idList.Count == 0) return;
        await EnsureSchemaAsync(cancellationToken);
        rank = MilitaryRankService.Canonicalize(rank);
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in idList)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE militares SET posto=$posto WHERE id=$id;";
            command.Parameters.AddWithValue("$posto", rank);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MilitaryDocumentRecord>> GetDocumentsAsync(int militaryId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var list = new List<MilitaryDocumentRecord>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,militar_id,tipo,titulo,caminho,nome_arquivo,data_salvo_br,observacao,chaves_json FROM militar_documentos WHERE militar_id=$id ORDER BY data_salvo DESC,id DESC;";
        command.Parameters.AddWithValue("$id", militaryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new MilitaryDocumentRecord
            {
                Id = GetInt(reader, "id"), MilitaryId = GetInt(reader, "militar_id"), Type = GetString(reader, "tipo", "DOCUMENTO"),
                Title = GetString(reader, "titulo"), Path = GetString(reader, "caminho"), FileName = GetString(reader, "nome_arquivo"),
                SavedAt = GetString(reader, "data_salvo_br"), Observation = GetString(reader, "observacao"), KeysJson = GetString(reader, "chaves_json")
            });
        }
        return list;
    }

    public async Task<Dictionary<string, string>> GetLatestCertificateKeysAsync(int militaryId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT chaves_json FROM militar_documentos WHERE militar_id=$id AND UPPER(tipo)='CERTIDAO_NASCIMENTO' AND COALESCE(chaves_json,'')<>'' ORDER BY data_salvo DESC,id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$id", militaryId);
        var json = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    public async Task<MilitaryDocumentRecord> AddDocumentAsync(MilitaryRecord military, string sourcePath, string type, string title, string observation = "", string keysJson = "", CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) throw new FileNotFoundException("Documento de origem não encontrado.", sourcePath);
        var folder = Path.Combine(_paths.DataDirectory, "documentos_militares", $"{military.Id:000000}_{SafeFileName(military.Name)}");
        Directory.CreateDirectory(folder);
        var extension = Path.GetExtension(sourcePath);
        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{SafeFileName(title)}{extension}";
        var destination = UniquePath(folder, fileName);
        await Task.Run(() => File.Copy(sourcePath, destination, false), cancellationToken);
        var now = DateTime.Now;
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO militar_documentos(militar_id,tipo,titulo,caminho,nome_arquivo,data_salvo,data_salvo_br,origem_pdf,observacao,chaves_json,criado_em)
            VALUES($mid,$tipo,$titulo,$caminho,$arquivo,$iso,$br,$origem,$observacao,$chaves,$iso); SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$mid", military.Id);
        command.Parameters.AddWithValue("$tipo", string.IsNullOrWhiteSpace(type) ? "DOCUMENTO" : type.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("$titulo", string.IsNullOrWhiteSpace(title) ? "Documento" : title.Trim());
        command.Parameters.AddWithValue("$caminho", destination);
        command.Parameters.AddWithValue("$arquivo", Path.GetFileName(destination));
        command.Parameters.AddWithValue("$iso", now.ToString("s"));
        command.Parameters.AddWithValue("$br", now.ToString("dd/MM/yyyy HH:mm"));
        command.Parameters.AddWithValue("$origem", Path.GetFullPath(sourcePath));
        command.Parameters.AddWithValue("$observacao", observation ?? string.Empty);
        command.Parameters.AddWithValue("$chaves", keysJson ?? string.Empty);
        var id = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return new MilitaryDocumentRecord { Id = id, MilitaryId = military.Id, Type = type, Title = title, Path = destination, FileName = Path.GetFileName(destination), SavedAt = now.ToString("dd/MM/yyyy HH:mm"), Observation = observation ?? string.Empty, KeysJson = keysJson ?? string.Empty };
    }

    public async Task UpdateDocumentOcrAsync(int documentId, string observation, string keysJson, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE militar_documentos SET observacao=$observacao,chaves_json=$chaves WHERE id=$id;";
        command.Parameters.AddWithValue("$observacao", observation ?? string.Empty);
        command.Parameters.AddWithValue("$chaves", keysJson ?? string.Empty);
        command.Parameters.AddWithValue("$id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveDocumentAsync(MilitaryDocumentRecord document, bool deletePhysicalFile, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM militar_documentos WHERE id=$id;";
        command.Parameters.AddWithValue("$id", document.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (deletePhysicalFile && File.Exists(document.Path)) File.Delete(document.Path);
    }

    public async Task<decimal> GetSalaryByRankAsync(string? rank, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);

        var original = (rank ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(original)) return 0m;

        var canonical = MilitaryRankService.Canonicalize(original);
        var shortRank = MilitaryRankService.ShortName(original);

        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT soldo
              FROM soldos_por_posto
             WHERE posto = $original COLLATE NOCASE
                OR posto = $canonical COLLATE NOCASE
                OR posto = $short COLLATE NOCASE
             ORDER BY CASE
                 WHEN posto = $original COLLATE NOCASE THEN 0
                 WHEN posto = $canonical COLLATE NOCASE THEN 1
                 ELSE 2
             END
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$original", original);
        command.Parameters.AddWithValue("$canonical", canonical);
        command.Parameters.AddWithValue("$short", shortRank);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull) return 0m;

        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.GetCultureInfo("pt-BR"), out var parsed)
                ? parsed
                : 0m;
        }
    }

    public async Task<TransportSummary> GetTransportSummaryAsync(MilitaryRecord military, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new TransportSummary
        {
            WorkingDays = military.TransportWorkingDays is > 0 ? military.TransportWorkingDays.Value : 22,
            BlockedByAttachedStatus = military.IsAttached,
            StoredNetPerMonth = ParseRouteMoney(military.TransportAidValue),
            StoredGrossPerMonth = Math.Max(0, military.TransportGrossTotal ?? 0)
        };
        await using var connection = OpenConnection();
        await using (var fare = connection.CreateCommand())
        {
            fare.CommandText = "SELECT idx,tarifa,linha,nome,categoria,url FROM aux_transporte_tarifas WHERE militar_id=$id ORDER BY idx;";
            fare.Parameters.AddWithValue("$id", military.Id);
            await using var reader = await fare.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Fares.Add(new TransportFareRecord
                {
                    Index = reader.GetInt32(0),
                    Fare = reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                    Number = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim().ToUpperInvariant(),
                    Name = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim().ToUpperInvariant(),
                    Category = reader.IsDBNull(4) ? string.Empty : reader.GetString(4).Trim().ToUpperInvariant(),
                    SourceUrl = reader.IsDBNull(5) ? string.Empty : reader.GetString(5).Trim()
                });
        }
        await using (var salary = connection.CreateCommand())
        {
            var rankSource = string.IsNullOrWhiteSpace(military.Rank) ? military.ShortRank : military.Rank;
            var canonicalRank = MilitaryRankService.Canonicalize(rankSource);
            var shortRank = MilitaryRankService.ShortName(rankSource);
            salary.CommandText = """
                SELECT soldo
                  FROM soldos_por_posto
                 WHERE posto = $original COLLATE NOCASE
                    OR posto = $canonical COLLATE NOCASE
                    OR posto = $short COLLATE NOCASE
                 LIMIT 1;
                """;
            salary.Parameters.AddWithValue("$original", rankSource ?? string.Empty);
            salary.Parameters.AddWithValue("$canonical", canonicalRank);
            salary.Parameters.AddWithValue("$short", shortRank);
            var value = await salary.ExecuteScalarAsync(cancellationToken);
            result.Salary = value is null or DBNull ? 0 : Convert.ToDouble(value);
        }

        // Módulo Python antigo salvava os detalhes em aux_transporte_rotas.sqlite3/rota_manual
        // e o valor líquido oficial em militares.valor_aux_transporte.
        // A carteira C# agora junta as duas fontes em UMA lista, sem criar um segundo quadro duplicado.
        var route = await GetTransportRouteDetailsAsync(military.Id, cancellationToken);

        if (result.Fares.Count > 0 && route.Buses.Count > 0)
        {
            var changed = false;
            for (var index = 0; index < result.Fares.Count; index++)
            {
                var fare = result.Fares[index];
                var routeBus = route.Buses.FirstOrDefault(x => x.Index == fare.Index)
                               ?? (index < route.Buses.Count ? route.Buses[index] : null);
                if (routeBus is null) continue;
                if (string.IsNullOrWhiteSpace(fare.Number) && !string.IsNullOrWhiteSpace(routeBus.Number)) { fare.Number = routeBus.Number; changed = true; }
                if (string.IsNullOrWhiteSpace(fare.Name) && !string.IsNullOrWhiteSpace(routeBus.Description)) { fare.Name = routeBus.Description; changed = true; }
                if (string.IsNullOrWhiteSpace(fare.Category) && !string.IsNullOrWhiteSpace(routeBus.Category)) { fare.Category = routeBus.Category; changed = true; }
                if (string.IsNullOrWhiteSpace(fare.SourceUrl) && !string.IsNullOrWhiteSpace(routeBus.SourceUrl)) { fare.SourceUrl = routeBus.SourceUrl; changed = true; }
            }

            foreach (var routeBus in route.Buses.Where(x => x.Fare > 0 && result.Fares.All(f => f.Index != x.Index)))
            {
                result.Fares.Add(new TransportFareRecord
                {
                    Index = result.Fares.Count,
                    Number = routeBus.Number,
                    Name = routeBus.Description,
                    Category = routeBus.Category,
                    SourceUrl = routeBus.SourceUrl,
                    Fare = routeBus.Fare
                });
                changed = true;
            }

            if (changed)
            {
                for (var index = 0; index < result.Fares.Count; index++) result.Fares[index].Index = index;
                await PersistTransportFareDetailsAsync(connection, military.Id, result.Fares, cancellationToken);
            }
        }

        // Migra automaticamente linhas antigas salvas somente no banco de rotas.
        if (result.Fares.Count == 0)
        {
            foreach (var bus in route.Buses.Where(x => x.Fare > 0))
                result.Fares.Add(new TransportFareRecord
                {
                    Index = bus.Index,
                    Number = bus.Number,
                    Name = bus.Description,
                    Category = bus.Category,
                    SourceUrl = bus.SourceUrl,
                    Fare = bus.Fare
                });

            if (result.Fares.Count > 0)
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    for (var index = 0; index < result.Fares.Count; index++)
                    {
                        var item = result.Fares[index];
                        item.Index = index;
                        await using var insert = connection.CreateCommand();
                        insert.Transaction = (SqliteTransaction)transaction;
                        insert.CommandText = "INSERT OR REPLACE INTO aux_transporte_tarifas(militar_id,idx,tarifa,linha,nome,categoria,url) VALUES($id,$idx,$fare,$linha,$nome,$categoria,$url);";
                        insert.Parameters.AddWithValue("$id", military.Id);
                        insert.Parameters.AddWithValue("$idx", index);
                        insert.Parameters.AddWithValue("$fare", item.Fare);
                        insert.Parameters.AddWithValue("$linha", item.Number ?? string.Empty);
                        insert.Parameters.AddWithValue("$nome", item.Name ?? string.Empty);
                        insert.Parameters.AddWithValue("$categoria", item.Category ?? string.Empty);
                        insert.Parameters.AddWithValue("$url", item.SourceUrl ?? string.Empty);
                        await insert.ExecuteNonQueryAsync(cancellationToken);
                    }
                    var gross = result.GrossPerMonth;
                    var net = result.NetPerMonth;
                    await using var update = connection.CreateCommand();
                    update.Transaction = (SqliteTransaction)transaction;
                    update.CommandText = "UPDATE militares SET recebe_aux_transporte='Sim',valor_aux_transporte=$valor,aux_total_bruto=$bruto,aux_dias_uteis=$dias,aux_base_ts=$ts WHERE id=$id;";
                    update.Parameters.AddWithValue("$valor", net.ToString("0.00", CultureInfo.InvariantCulture));
                    update.Parameters.AddWithValue("$bruto", gross);
                    update.Parameters.AddWithValue("$dias", result.WorkingDays);
                    update.Parameters.AddWithValue("$ts", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
                    update.Parameters.AddWithValue("$id", military.Id);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    military.ReceivesTransportAid = "Sim";
                    military.TransportAidValue = net.ToString("0.00", CultureInfo.InvariantCulture);
                    result.StoredNetPerMonth = net;
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            }
        }
        return result;
    }

    public async Task<TransportRouteDetails> GetTransportRouteDetailsAsync(int militaryId, CancellationToken cancellationToken = default)
    {
        var result = new TransportRouteDetails();
        if (!File.Exists(_paths.TransportRoutesDatabaseFile)) return result;

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _paths.TransportRoutesDatabaseFile,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
                DefaultTimeout = 8
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT origem,destino,linha,linha_nome,categoria,tarifas_json,onibus_json
                      FROM rota_manual
                     WHERE militar_id=$id
                     LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$id", militaryId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    result.Origin = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                    result.Destination = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                    var baseNumber = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                    var baseName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
                    var category = reader.IsDBNull(4) ? string.Empty : reader.GetString(4).Trim();
                    var faresJson = reader.IsDBNull(5) ? "[]" : reader.GetString(5);
                    var busesJson = reader.IsDBNull(6) ? "[]" : reader.GetString(6);
                    ParseRouteBusesJson(result, busesJson, baseNumber, baseName, category);
                    if (result.Buses.Count == 0)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(faresJson) ? "[]" : faresJson);
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                var index = 0;
                                foreach (var item in doc.RootElement.EnumerateArray())
                                {
                                    var fare = ParseRouteMoney(item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString());
                                    if (fare <= 0) continue;
                                    result.Buses.Add(new BusRouteRecord
                                    {
                                        Index = index++,
                                        Number = baseNumber.ToUpperInvariant(),
                                        Description = (string.IsNullOrWhiteSpace(baseName) ? category : baseName).ToUpperInvariant(),
                                        Category = category.ToUpperInvariant(),
                                        Fare = fare
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (SqliteException) { }

            if (result.Buses.Count == 0)
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT origem,destino,linhas_json
                          FROM rotas_militar
                         WHERE militar_id=$id
                         LIMIT 1;
                        """;
                    command.Parameters.AddWithValue("$id", militaryId);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        if (string.IsNullOrWhiteSpace(result.Origin)) result.Origin = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                        if (string.IsNullOrWhiteSpace(result.Destination)) result.Destination = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                        ParseRouteBusesJson(result, reader.IsDBNull(2) ? "[]" : reader.GetString(2), string.Empty, string.Empty, string.Empty);
                    }
                }
                catch (SqliteException) { }
            }
            result.RouteDescription = string.Join(" — ", new[] { result.Origin, result.Destination }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"Falha lendo a rota do militar {militaryId}.", ex);
        }
        return result;
    }

    private static void ParseRouteBusesJson(TransportRouteDetails result, string json, string baseNumber, string baseName, string baseCategory)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            var index = result.Buses.Count;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var number = JsonText(item, "linha", "numero", "onibus", "number");
                var description = JsonText(item, "linha_nome", "descricao", "bairro", "nome", "name", "description");
                var category = JsonText(item, "categoria", "category");
                var sourceUrl = JsonText(item, "url", "link", "source_url", "sourceUrl");
                var fare = JsonMoney(item, "valor", "tarifa", "fare");
                if (string.IsNullOrWhiteSpace(number)) number = baseNumber;
                if (string.IsNullOrWhiteSpace(description)) description = baseName;
                if (string.IsNullOrWhiteSpace(category)) category = baseCategory;
                if (string.IsNullOrWhiteSpace(description)) description = category;
                if (fare <= 0 && string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(description)) continue;
                result.Buses.Add(new BusRouteRecord
                {
                    Index = index++,
                    Number = number.Trim().ToUpperInvariant(),
                    Description = description.Trim().ToUpperInvariant(),
                    Category = category.Trim().ToUpperInvariant(),
                    SourceUrl = sourceUrl.Trim(),
                    Fare = fare
                });
            }
        }
        catch { }
    }

    private static string JsonText(JsonElement item, params string[] names)
    {
        foreach (var property in item.EnumerateObject())
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return property.Value.ToString().Trim();
        return string.Empty;
    }

    private static double JsonMoney(JsonElement item, params string[] names)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (!names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
            var parsed = ParseRouteMoney(value.ToString());
            if (parsed > 0) return parsed;
        }
        return 0;
    }

    private static double ParseRouteMoney(string? value)
    {
        var text = (value ?? string.Empty).Trim().Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        // O banco Python grava "630.21" com ponto decimal. Tentar pt-BR primeiro transforma
        // esse valor em 63.021,00, pois o ponto é interpretado como separador de milhar.
        if (!text.Contains(',') && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var inv)) return inv;
        if (double.TryParse(text, NumberStyles.Currency, CultureInfo.GetCultureInfo("pt-BR"), out var br)) return br;
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out inv)) return inv;
        return 0;
    }

    private static async Task PersistTransportFareDetailsAsync(
        SqliteConnection connection,
        int militaryId,
        IReadOnlyList<TransportFareRecord> fares,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in fares)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "INSERT OR REPLACE INTO aux_transporte_tarifas(militar_id,idx,tarifa,linha,nome,categoria,url) VALUES($id,$idx,$fare,$linha,$nome,$categoria,$url);";
                command.Parameters.AddWithValue("$id", militaryId);
                command.Parameters.AddWithValue("$idx", item.Index);
                command.Parameters.AddWithValue("$fare", item.Fare);
                command.Parameters.AddWithValue("$linha", item.Number ?? string.Empty);
                command.Parameters.AddWithValue("$nome", item.Name ?? string.Empty);
                command.Parameters.AddWithValue("$categoria", item.Category ?? string.Empty);
                command.Parameters.AddWithValue("$url", item.SourceUrl ?? string.Empty);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task SaveTransportFaresAsync(MilitaryRecord military, IEnumerable<double> fares, int workingDays, CancellationToken cancellationToken = default)
        => SaveTransportFaresAsync(
            military,
            fares.Select((fare, index) => new TransportFareRecord { Index = index, Fare = Math.Max(0, fare) }),
            workingDays,
            cancellationToken);

    public async Task SaveTransportFaresAsync(MilitaryRecord military, IEnumerable<TransportFareRecord> fares, int workingDays, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var values = fares
            .Select((x, index) => new TransportFareRecord
            {
                Index = index,
                Number = (x.Number ?? string.Empty).Trim().ToUpperInvariant(),
                Name = (x.Name ?? string.Empty).Trim().ToUpperInvariant(),
                Category = (x.Category ?? string.Empty).Trim().ToUpperInvariant(),
                SourceUrl = (x.SourceUrl ?? string.Empty).Trim(),
                Fare = Math.Max(0, x.Fare)
            })
            .Where(x => x.Fare > 0)
            .ToList();

        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM aux_transporte_tarifas WHERE militar_id=$id;";
            clear.Parameters.AddWithValue("$id", military.Id);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        for (var index = 0; index < values.Count; index++)
        {
            var item = values[index];
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO aux_transporte_tarifas(militar_id,idx,tarifa,linha,nome,categoria,url) VALUES($id,$idx,$fare,$linha,$nome,$categoria,$url);";
            insert.Parameters.AddWithValue("$id", military.Id);
            insert.Parameters.AddWithValue("$idx", index);
            insert.Parameters.AddWithValue("$fare", item.Fare);
            insert.Parameters.AddWithValue("$linha", item.Number);
            insert.Parameters.AddWithValue("$nome", item.Name);
            insert.Parameters.AddWithValue("$categoria", item.Category);
            insert.Parameters.AddWithValue("$url", item.SourceUrl);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var gross = values.Sum(x => x.Fare) * 2 * Math.Max(0, workingDays);
        var salary = await GetSalaryWithinTransactionAsync(connection, (SqliteTransaction)transaction, military.Rank, cancellationToken);
        var share = salary * 0.06 * (Math.Max(0, workingDays) / 30.0);
        var net = military.IsAttached ? 0 : Math.Max(0, gross - share);
        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = "UPDATE militares SET recebe_aux_transporte=$recebe,valor_aux_transporte=$valor,aux_total_bruto=$bruto,aux_dias_uteis=$dias,aux_base_ts=$ts WHERE id=$id;";
        update.Parameters.AddWithValue("$recebe", military.IsAttached || values.Count == 0 ? "Não" : "Sim");
        update.Parameters.AddWithValue("$valor", net.ToString("0.00", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$bruto", gross);
        update.Parameters.AddWithValue("$dias", Math.Max(0, workingDays));
        update.Parameters.AddWithValue("$ts", DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$id", military.Id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ServiceIntervalRecord>> GetServiceIntervalsAsync(int militaryId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var list = new List<ServiceIntervalRecord>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,militar_id,data_inicio,data_fim,observacao,ordem,ativo FROM tempo_servico_intervalos WHERE militar_id=$id AND ativo=1 ORDER BY ordem,id;";
        command.Parameters.AddWithValue("$id", militaryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) list.Add(new ServiceIntervalRecord
        {
            Id = reader.GetInt32(0), MilitaryId = reader.GetInt32(1), StartDate = reader.GetString(2), EndDate = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Observation = reader.IsDBNull(4) ? string.Empty : reader.GetString(4), Order = reader.IsDBNull(5) ? 0 : reader.GetInt32(5), Active = reader.IsDBNull(6) || reader.GetInt32(6) != 0
        });
        return list;
    }

    public async Task<IReadOnlyDictionary<int, List<ServiceIntervalRecord>>> GetAllServiceIntervalsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new Dictionary<int, List<ServiceIntervalRecord>>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,militar_id,data_inicio,data_fim,observacao,ordem,ativo FROM tempo_servico_intervalos WHERE ativo=1 ORDER BY militar_id,ordem,id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new ServiceIntervalRecord
            {
                Id = reader.GetInt32(0), MilitaryId = reader.GetInt32(1), StartDate = reader.GetString(2),
                EndDate = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Observation = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                Order = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                Active = reader.IsDBNull(6) || reader.GetInt32(6) != 0
            };
            if (!result.TryGetValue(item.MilitaryId, out var list))
            {
                list = [];
                result[item.MilitaryId] = list;
            }
            list.Add(item);
        }
        return result;
    }

    public async Task SaveServiceIntervalAsync(ServiceIntervalRecord interval, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        if (MilitaryFormatting.ParseDate(interval.StartDate) is null) throw new InvalidOperationException("Informe uma data inicial válida.");
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = interval.Id <= 0
            ? "INSERT INTO tempo_servico_intervalos(militar_id,data_inicio,data_fim,observacao,ordem,ativo,criado_em,atualizado_em) VALUES($mid,$ini,$fim,$obs,$ordem,1,$ts,$ts);"
            : "UPDATE tempo_servico_intervalos SET data_inicio=$ini,data_fim=$fim,observacao=$obs,ordem=$ordem,ativo=1,atualizado_em=$ts WHERE id=$id;";
        command.Parameters.AddWithValue("$mid", interval.MilitaryId);
        command.Parameters.AddWithValue("$ini", MilitaryFormatting.NormalizeDateText(interval.StartDate));
        command.Parameters.AddWithValue("$fim", string.IsNullOrWhiteSpace(interval.EndDate) ? string.Empty : MilitaryFormatting.NormalizeDateText(interval.EndDate));
        command.Parameters.AddWithValue("$obs", interval.Observation?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$ordem", interval.Order);
        command.Parameters.AddWithValue("$ts", DateTime.Now.ToString("s"));
        command.Parameters.AddWithValue("$id", interval.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteServiceIntervalAsync(int id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tempo_servico_intervalos WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateContactAsync(int militaryId, string phone, string? email = null, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var military = await GetByIdAsync(militaryId, cancellationToken)
            ?? throw new InvalidOperationException("Militar não encontrado no Listar Militares.");
        military.Phone = phone?.Trim() ?? string.Empty;
        if (email is not null) military.Email = email.Trim();
        await using var connection = OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertContactAsync(connection, transaction, military, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static MilitaryRecord ReadMilitary(SqliteDataReader reader) => new()
    {
        Id = GetInt(reader, "id"), Rank = MilitaryRankService.Canonicalize(GetString(reader, "posto")), Name = UpperName(GetString(reader, "nome")), WarName = UpperName(GetString(reader, "nome_guerra")),
        Cpf = GetString(reader, "cpf"), PrecCp = GetString(reader, "prec_cp"), MilitaryId = GetString(reader, "idt"), Bank = GetString(reader, "banco"),
        Agency = GetString(reader, "agencia"), Account = GetString(reader, "conta"), PhotoPath = GetString(reader, "foto"), FormationYear = GetString(reader, "ano"),
        BirthDate = GetString(reader, "data_nascimento"), EnlistmentDate = GetString(reader, "data_praca"), Address = GetString(reader, "endereco"), ZipCode = GetString(reader, "cep"),
        ReceivesPreSchool = GetString(reader, "recebe_pre_escolar", "Não"), PreSchoolValue = GetString(reader, "valor_pre_escolar", "0.00"),
        ReceivesTransportAid = GetString(reader, "recebe_aux_transporte", "Não"), TransportAidValue = GetString(reader, "valor_aux_transporte", "0.00"),
        HasPnr = GetString(reader, "pnr", "Não"), Alimony = GetString(reader, "pensao_alimenticia", "Não"), AlimonyValue = GetString(reader, "valor_pensao"),
        TransportGrossTotal = GetNullableDouble(reader, "aux_total_bruto"), TransportWorkingDays = GetNullableInt(reader, "aux_dias_uteis"), TransportBaseTimestamp = GetString(reader, "aux_base_ts"),
        Phone = GetString(reader, "contato_telefone"), Email = GetString(reader, "contato_email"), Education = GetString(reader, "contato_escolaridade"),
        IsAttached = GetBool(reader, "adido_encostado")
    };

    private async Task ValidateUniqueAsync(SqliteConnection connection, SqliteTransaction transaction, MilitaryRecord military, CancellationToken cancellationToken)
    {
        foreach (var (column, rawValue, label) in new[] { ("cpf", military.Cpf, "CPF"), ("prec_cp", military.PrecCp, "PREC-CP"), ("idt", military.MilitaryId, "IDT") })
        {
            var value = NormalizeIdentifier(rawValue);
            if (string.IsNullOrWhiteSpace(value)) continue;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT id FROM militares WHERE UPPER(REPLACE(REPLACE(REPLACE(REPLACE(COALESCE({column},''),'.',''),'-',''),' ',''),'/',''))=$value AND id<>$id LIMIT 1;";
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$id", military.Id);
            if (await command.ExecuteScalarAsync(cancellationToken) is not null) throw new InvalidOperationException($"Já existe outro militar com o mesmo {label}.");
        }
    }

    private static void ValidateMilitary(MilitaryRecord military)
    {
        if (string.IsNullOrWhiteSpace(military.Rank)) throw new InvalidOperationException("Informe o posto/graduação.");
        if (string.IsNullOrWhiteSpace(military.Name)) throw new InvalidOperationException("Informe o nome completo.");
        if (string.IsNullOrWhiteSpace(military.WarName)) throw new InvalidOperationException("Informe o nome de guerra.");
        var cpf = MilitaryFormatting.Digits(military.Cpf);
        if (cpf.Length != 11) throw new InvalidOperationException("O CPF deve possuir 11 dígitos.");
        if (string.IsNullOrWhiteSpace(military.PrecCp)) throw new InvalidOperationException("Informe o PREC-CP.");
        if (string.IsNullOrWhiteSpace(military.MilitaryId)) throw new InvalidOperationException("Informe a identidade militar.");
    }

    private static async Task UpsertContactAsync(SqliteConnection connection, SqliteTransaction transaction, MilitaryRecord military, CancellationToken cancellationToken)
    {
        var cpf = MilitaryFormatting.Digits(military.Cpf);
        var now = DateTime.Now.ToString("s");
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO militares_contato(militar_id,cpf,telefone,email,atualizado_em) VALUES($id,$cpf,$tel,$email,$ts)
                ON CONFLICT(militar_id) DO UPDATE SET cpf=excluded.cpf,telefone=excluded.telefone,email=excluded.email,atualizado_em=excluded.atualizado_em;
                """;
            command.Parameters.AddWithValue("$id", military.Id);
            command.Parameters.AddWithValue("$cpf", cpf);
            command.Parameters.AddWithValue("$tel", military.Phone.Trim());
            command.Parameters.AddWithValue("$email", military.Email.Trim());
            command.Parameters.AddWithValue("$esc", military.Education.Trim());
            command.Parameters.AddWithValue("$ts", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO recrutas_extra(cpf,telefone,email,escolaridade,criado_em) VALUES($cpf,$tel,$email,$esc,$ts)
                ON CONFLICT(cpf) DO UPDATE SET telefone=excluded.telefone,email=excluded.email,escolaridade=CASE WHEN excluded.escolaridade<>'' THEN excluded.escolaridade ELSE recrutas_extra.escolaridade END;
                """;
            command.Parameters.AddWithValue("$cpf", cpf);
            command.Parameters.AddWithValue("$tel", military.Phone.Trim());
            command.Parameters.AddWithValue("$email", military.Email.Trim());
            command.Parameters.AddWithValue("$esc", military.Education.Trim());
            command.Parameters.AddWithValue("$ts", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddMilitaryParameters(SqliteCommand command, MilitaryRecord military)
    {
        command.Parameters.AddWithValue("$posto", MilitaryRankService.Canonicalize(military.Rank));
        command.Parameters.AddWithValue("$nome", UpperName(military.Name));
        command.Parameters.AddWithValue("$ng", UpperName(military.WarName));
        command.Parameters.AddWithValue("$cpf", MilitaryFormatting.Digits(military.Cpf));
        command.Parameters.AddWithValue("$prec", military.PrecCp);
        command.Parameters.AddWithValue("$idt", military.MilitaryId);
        command.Parameters.AddWithValue("$banco", military.Bank);
        command.Parameters.AddWithValue("$agencia", military.Agency);
        command.Parameters.AddWithValue("$conta", military.Account);
        command.Parameters.AddWithValue("$foto", military.PhotoPath);
        command.Parameters.AddWithValue("$ano", military.FormationYear);
        command.Parameters.AddWithValue("$nascimento", MilitaryFormatting.NormalizeDateText(military.BirthDate));
        command.Parameters.AddWithValue("$praca", MilitaryFormatting.NormalizeDateText(military.EnlistmentDate));
        command.Parameters.AddWithValue("$endereco", military.Address);
        command.Parameters.AddWithValue("$cep", MilitaryFormatting.Digits(military.ZipCode));
        command.Parameters.AddWithValue("$rpe", military.ReceivesPreSchool);
        command.Parameters.AddWithValue("$vpe", military.PreSchoolValue);
        command.Parameters.AddWithValue("$rat", military.ReceivesTransportAid);
        command.Parameters.AddWithValue("$vat", military.TransportAidValue);
        command.Parameters.AddWithValue("$pnr", military.HasPnr);
    }

    private static async Task<int> NextLicensedNegativeIdAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MIN(id),0) FROM lt_militares WHERE id<0;";
        var current = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture);
        return current >= 0 ? -1 : checked(current - 1);
    }

    private static async Task CopyLinkedDataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int sourceId,
        int destinationId,
        MilitaryRecord military,
        CancellationToken cancellationToken)
    {
        await using (var fares = connection.CreateCommand())
        {
            fares.Transaction = transaction;
            fares.CommandText = "INSERT OR REPLACE INTO aux_transporte_tarifas(militar_id,idx,tarifa,linha,nome,categoria,url) SELECT $dst,idx,tarifa,COALESCE(linha,''),COALESCE(nome,''),COALESCE(categoria,''),COALESCE(url,'') FROM aux_transporte_tarifas WHERE militar_id=$src;";
            fares.Parameters.AddWithValue("$src", sourceId);
            fares.Parameters.AddWithValue("$dst", destinationId);
            await fares.ExecuteNonQueryAsync(cancellationToken);
        }

        // SQLite pode interpretar ON CONFLICT após INSERT ... SELECT como parte do SELECT.
        // O contato é copiado por valores para manter compatibilidade com todas as versões.
        var phone = military.Phone;
        var email = military.Email;
        var cpf = MilitaryFormatting.Digits(military.Cpf);
        await using (var readContact = connection.CreateCommand())
        {
            readContact.Transaction = transaction;
            readContact.CommandText = "SELECT cpf,telefone,email FROM militares_contato WHERE militar_id=$src LIMIT 1;";
            readContact.Parameters.AddWithValue("$src", sourceId);
            await using var reader = await readContact.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                cpf = reader.IsDBNull(0) ? cpf : reader.GetString(0);
                phone = reader.IsDBNull(1) ? phone : reader.GetString(1);
                email = reader.IsDBNull(2) ? email : reader.GetString(2);
            }
        }
        await using (var contact = connection.CreateCommand())
        {
            contact.Transaction = transaction;
            contact.CommandText = """
                INSERT INTO militares_contato(militar_id,cpf,telefone,email,atualizado_em)
                VALUES($dst,$cpf,$phone,$email,$timestamp)
                ON CONFLICT(militar_id) DO UPDATE SET
                    cpf=excluded.cpf, telefone=excluded.telefone, email=excluded.email, atualizado_em=excluded.atualizado_em;
                """;
            contact.Parameters.AddWithValue("$dst", destinationId);
            contact.Parameters.AddWithValue("$cpf", cpf ?? string.Empty);
            contact.Parameters.AddWithValue("$phone", phone ?? string.Empty);
            contact.Parameters.AddWithValue("$email", email ?? string.Empty);
            contact.Parameters.AddWithValue("$timestamp", DateTime.Now.ToString("s"));
            await contact.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var serviceIntervals = connection.CreateCommand())
        {
            serviceIntervals.Transaction = transaction;
            serviceIntervals.CommandText = """
                INSERT INTO tempo_servico_intervalos(
                    militar_id,data_inicio,data_fim,observacao,ordem,ativo,criado_em,atualizado_em)
                SELECT $dst,data_inicio,data_fim,observacao,ordem,ativo,criado_em,atualizado_em
                  FROM tempo_servico_intervalos WHERE militar_id=$src;
                """;
            serviceIntervals.Parameters.AddWithValue("$src", sourceId);
            serviceIntervals.Parameters.AddWithValue("$dst", destinationId);
            await serviceIntervals.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var documents = connection.CreateCommand())
        {
            documents.Transaction = transaction;
            documents.CommandText = """
                INSERT INTO militar_documentos(
                    militar_id,tipo,titulo,caminho,nome_arquivo,data_salvo,data_salvo_br,
                    origem_pdf,observacao,chaves_json,criado_em)
                SELECT $dst,tipo,titulo,caminho,nome_arquivo,data_salvo,data_salvo_br,
                       origem_pdf,observacao,chaves_json,criado_em
                  FROM militar_documentos WHERE militar_id=$src;
                """;
            documents.Parameters.AddWithValue("$src", sourceId);
            documents.Parameters.AddWithValue("$dst", destinationId);
            await documents.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<double> GetSalaryWithinTransactionAsync(SqliteConnection connection, SqliteTransaction transaction, string rank, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT soldo FROM soldos_por_posto WHERE posto=$posto LIMIT 1;";
        command.Parameters.AddWithValue("$posto", rank);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToDouble(value);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(SqliteConnection connection, string table, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(1));
        return result;
    }

    private static int RankOrder(string rank) => MilitaryRankService.GetOrder(rank);

    private static string NormalizeYesNo(string? value) => MilitaryRecord.IsYes(value) ? "Sim" : "Não";
    private static string NormalizeIdentifier(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string SafeFileName(string value)
    {
        var cleaned = string.Join('_', (value ?? "documento").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        cleaned = string.Join('_', cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "documento" : cleaned[..Math.Min(100, cleaned.Length)];
    }

    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; ; n++)
        {
            path = Path.Combine(folder, $"{baseName} ({n}){extension}");
            if (!File.Exists(path)) return path;
        }
    }

    private static string GetString(SqliteDataReader reader, string name, string fallback = "")
    {
        try { var ordinal = reader.GetOrdinal(name); return reader.IsDBNull(ordinal) ? fallback : Convert.ToString(reader.GetValue(ordinal)) ?? fallback; }
        catch (IndexOutOfRangeException) { return fallback; }
    }
    private static int GetInt(SqliteDataReader reader, string name)
    {
        try { var ordinal = reader.GetOrdinal(name); return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal)); }
        catch (IndexOutOfRangeException) { return 0; }
    }
    private static double? GetNullableDouble(SqliteDataReader reader, string name)
    {
        try { var ordinal = reader.GetOrdinal(name); return reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal)); }
        catch (IndexOutOfRangeException) { return null; }
    }
    private static int? GetNullableInt(SqliteDataReader reader, string name)
    {
        try { var ordinal = reader.GetOrdinal(name); return reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal)); }
        catch (IndexOutOfRangeException) { return null; }
    }
    private static bool GetBool(SqliteDataReader reader, string name)
    {
        try
        {
            var ordinal = reader.GetOrdinal(name);
            if (reader.IsDBNull(ordinal)) return false;
            var value = reader.GetValue(ordinal);
            if (value is bool flag) return flag;
            if (value is byte or short or int or long) return Convert.ToInt64(value) != 0;
            var text = Convert.ToString(value)?.Trim();
            return text is not null && (text.Equals("sim", StringComparison.OrdinalIgnoreCase)
                || text.Equals("true", StringComparison.OrdinalIgnoreCase)
                || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || text == "1");
        }
        catch (IndexOutOfRangeException) { return false; }
    }
}
