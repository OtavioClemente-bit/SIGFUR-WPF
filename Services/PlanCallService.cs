using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Plano de Chamada com banco próprio. O banco de militares é consultado somente
/// para comparar endereços e, por ação explícita, completar telefone que esteja vazio.
/// </summary>
public sealed class PlanCallService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly MilitaryRepository _military;
    private readonly LogService _log;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public PlanCallService(AppPaths paths, JsonFileService json, MilitaryRepository military, LogService log)
    {
        _paths = paths;
        _json = json;
        _military = military;
        _log = log;
    }

    private SqliteConnection OpenConnection()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.PlanCallDatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 12
        };
        var connection = new SqliteConnection(cs.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=12000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS plano_pessoas(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    posto TEXT DEFAULT '',
                    nome TEXT NOT NULL DEFAULT '',
                    nome_guerra TEXT DEFAULT '',
                    cpf TEXT DEFAULT '',
                    prec_cp TEXT DEFAULT '',
                    telefone TEXT DEFAULT '',
                    telefone_alt TEXT DEFAULT '',
                    rua TEXT DEFAULT '',
                    numero TEXT DEFAULT '',
                    complemento TEXT DEFAULT '',
                    bairro TEXT DEFAULT '',
                    cidade_uf TEXT DEFAULT '',
                    cep TEXT DEFAULT '',
                    fonte_importacao TEXT DEFAULT '',
                    criado_em TEXT DEFAULT CURRENT_TIMESTAMP,
                    atualizado_em TEXT DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS ix_plano_pessoas_nome ON plano_pessoas(nome);
                CREATE INDEX IF NOT EXISTS ix_plano_pessoas_cpf ON plano_pessoas(cpf);
                CREATE INDEX IF NOT EXISTS ix_plano_pessoas_prec ON plano_pessoas(prec_cp);
                CREATE TABLE IF NOT EXISTS plano_restore(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    criado_em TEXT NOT NULL,
                    descricao TEXT DEFAULT '',
                    snapshot_json TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await MigrateLegacyDataAsync(connection, cancellationToken);
            _schemaReady = true;
        }
        finally { _schemaGate.Release(); }
    }

    private async Task MigrateLegacyDataAsync(SqliteConnection destination, CancellationToken cancellationToken)
    {
        // A migração roda de forma idempotente em todas as aberturas da primeira
        // sessão do serviço. InsertLegacyRecordAsync elimina duplicidades, portanto
        // registros antigos ainda são recuperados mesmo quando o banco WPF já contém
        // uma ou duas pessoas cadastradas manualmente.
        var imported = 0;
        foreach (var jsonPath in LegacyPlanCallJsonCandidates())
        {
            if (!File.Exists(jsonPath)) continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, cancellationToken));
                foreach (var element in EnumerateLegacyObjects(document.RootElement))
                {
                    var record = LegacyRecordFromJson(element, Path.GetFileName(jsonPath));
                    if (record is not null && await InsertLegacyRecordAsync(destination, record, cancellationToken)) imported++;
                }
            }
            catch (Exception ex) { await _log.WriteAsync($"Plano de Chamada: não foi possível migrar {jsonPath}.", ex); }
        }

        foreach (var database in LegacyPlanCallDatabaseCandidates())
        {
            if (!File.Exists(database) || Path.GetFullPath(database).Equals(Path.GetFullPath(_paths.PlanCallDatabaseFile), StringComparison.OrdinalIgnoreCase)) continue;
            try { imported += await ImportLegacyDatabaseAsync(destination, database, cancellationToken); }
            catch (Exception ex) { await _log.WriteAsync($"Plano de Chamada: não foi possível migrar {database}.", ex); }
        }

        if (imported > 0) await _log.WriteAsync($"Plano de Chamada: {imported} registro(s) antigos recuperados automaticamente.");
    }

    private IEnumerable<string> LegacyPlanCallJsonCandidates()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var names = new[] { "plano_chamada.json", "plano_de_chamada.json", "plano_chamada_dados.json", "plano_chamada_pessoas.json", "plan_call.json" };
        foreach (var name in names)
        {
            yield return Path.Combine(_paths.DataDirectory, name);
            yield return Path.Combine(documents, "SIGFUR", name);
            yield return Path.Combine(documents, "SIGFUR", "Plano de Chamada", name);
        }
    }

    private IEnumerable<string> LegacyPlanCallDatabaseCandidates()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var names = new[] { "plano_chamada.db", "plano_chamada.sqlite", "plano_de_chamada.db", "plan_call.db", "militares.db" };
        foreach (var name in names)
        {
            yield return Path.Combine(_paths.DataDirectory, name);
            yield return Path.Combine(documents, "SIGFUR", name);
            yield return Path.Combine(documents, "SIGFUR", "Plano de Chamada", name);
        }
    }

    private static IEnumerable<JsonElement> EnumerateLegacyObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                foreach (var nested in EnumerateLegacyObjects(item)) yield return nested;
            yield break;
        }
        if (root.ValueKind != JsonValueKind.Object) yield break;
        var hasName = root.EnumerateObject().Any(x => NormalizeLegacyKey(x.Name) is "nome" or "nomecompleto");
        if (hasName) yield return root;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                foreach (var nested in EnumerateLegacyObjects(property.Value)) yield return nested;
        }
    }

    private static PlanCallRecord? LegacyRecordFromJson(JsonElement element, string source)
    {
        string Get(params string[] aliases)
        {
            var wanted = aliases.Select(NormalizeLegacyKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!wanted.Contains(NormalizeLegacyKey(property.Name))) continue;
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
            }
            return string.Empty;
        }
        var name = Get("nome", "nome_completo", "militar", "pessoa").Trim();
        if (name.Length < 3) return null;
        return new PlanCallRecord
        {
            Rank = Get("posto", "posto_graduacao", "pg", "graduacao"), Name = name, WarName = Get("nome_guerra", "ng"),
            Cpf = Get("cpf"), PrecCp = Get("prec_cp", "prec", "preccp"), Phone = Get("telefone", "celular", "fone"),
            AlternatePhone = Get("telefone_alt", "telefone_alternativo", "fone2", "celular2"), Street = Get("rua", "logradouro"),
            Number = Get("numero", "nr"), Complement = Get("complemento"), District = Get("bairro"),
            CityState = Get("cidade_uf", "cidade", "municipio"), ZipCode = Get("cep"), ImportSource = $"Migração automática: {source}", HasOverride = true
        };
    }

    private async Task<int> ImportLegacyDatabaseAsync(SqliteConnection destination, string database, CancellationToken cancellationToken)
    {
        var imported = 0;
        var cs = new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        await using var source = new SqliteConnection(cs.ToString());
        await source.OpenAsync(cancellationToken);
        var tables = new List<string>();
        await using (var tableCommand = source.CreateCommand())
        {
            tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        }
        foreach (var table in tables.Where(x => NormalizeLegacyKey(x).Contains("plano") || NormalizeLegacyKey(x).Contains("chamada")))
        {
            var columns = new List<string>();
            await using (var columnCommand = source.CreateCommand())
            {
                columnCommand.CommandText = $"PRAGMA table_info([{table.Replace("]", "]]", StringComparison.Ordinal)}]);";
                await using var reader = await columnCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) columns.Add(reader.GetString(1));
            }
            if (!columns.Any(x => NormalizeLegacyKey(x) is "nome" or "nomecompleto")) continue;
            await using var rows = source.CreateCommand();
            rows.CommandText = $"SELECT * FROM [{table.Replace("]", "]]", StringComparison.Ordinal)}];";
            await using var data = await rows.ExecuteReaderAsync(cancellationToken);
            while (await data.ReadAsync(cancellationToken))
            {
                string Get(params string[] aliases)
                {
                    var wanted = aliases.Select(NormalizeLegacyKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    for (var index = 0; index < data.FieldCount; index++)
                        if (wanted.Contains(NormalizeLegacyKey(data.GetName(index))) && !data.IsDBNull(index)) return Convert.ToString(data.GetValue(index), CultureInfo.CurrentCulture) ?? string.Empty;
                    return string.Empty;
                }
                var name = Get("nome", "nome_completo", "militar", "pessoa").Trim();
                if (name.Length < 3) continue;
                var record = new PlanCallRecord
                {
                    Rank = Get("posto", "posto_graduacao", "pg", "graduacao"), Name = name, WarName = Get("nome_guerra", "ng"),
                    Cpf = Get("cpf"), PrecCp = Get("prec_cp", "prec", "preccp"), Phone = Get("telefone", "celular", "fone"),
                    AlternatePhone = Get("telefone_alt", "telefone_alternativo", "fone2", "celular2"), Street = Get("rua", "logradouro"),
                    Number = Get("numero", "nr"), Complement = Get("complemento"), District = Get("bairro"),
                    CityState = Get("cidade_uf", "cidade", "municipio"), ZipCode = Get("cep"), ImportSource = $"Migração automática: {Path.GetFileName(database)}/{table}", HasOverride = true
                };
                if (await InsertLegacyRecordAsync(destination, record, cancellationToken)) imported++;
            }
        }
        return imported;
    }

    private static async Task<bool> InsertLegacyRecordAsync(SqliteConnection destination, PlanCallRecord record, CancellationToken cancellationToken)
    {
        var cpf = Digits(record.Cpf); var prec = Digits(record.PrecCp);
        await using (var exists = destination.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(1) FROM plano_pessoas WHERE ($cpf<>'' AND REPLACE(REPLACE(REPLACE(cpf,'.',''),'-',''),' ','')=$cpf) OR ($prec<>'' AND REPLACE(REPLACE(prec_cp,'-',''),' ','')=$prec) OR UPPER(TRIM(nome))=UPPER(TRIM($nome));";
            exists.Parameters.AddWithValue("$cpf", cpf); exists.Parameters.AddWithValue("$prec", prec); exists.Parameters.AddWithValue("$nome", record.Name);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0) return false;
        }
        await using var insert = destination.CreateCommand();
        insert.CommandText = "INSERT INTO plano_pessoas(posto,nome,nome_guerra,cpf,prec_cp,telefone,telefone_alt,rua,numero,complemento,bairro,cidade_uf,cep,fonte_importacao,criado_em,atualizado_em) VALUES($posto,$nome,$guerra,$cpf,$prec,$tel,$alt,$rua,$num,$comp,$bairro,$cidade,$cep,$fonte,$agora,$agora);";
        insert.Parameters.AddWithValue("$posto", record.Rank ?? string.Empty); insert.Parameters.AddWithValue("$nome", record.Name ?? string.Empty);
        insert.Parameters.AddWithValue("$guerra", record.WarName ?? string.Empty); insert.Parameters.AddWithValue("$cpf", record.Cpf ?? string.Empty);
        insert.Parameters.AddWithValue("$prec", record.PrecCp ?? string.Empty); insert.Parameters.AddWithValue("$tel", record.Phone ?? string.Empty);
        insert.Parameters.AddWithValue("$alt", record.AlternatePhone ?? string.Empty); insert.Parameters.AddWithValue("$rua", record.Street ?? string.Empty);
        insert.Parameters.AddWithValue("$num", record.Number ?? string.Empty); insert.Parameters.AddWithValue("$comp", record.Complement ?? string.Empty);
        insert.Parameters.AddWithValue("$bairro", record.District ?? string.Empty); insert.Parameters.AddWithValue("$cidade", record.CityState ?? string.Empty);
        insert.Parameters.AddWithValue("$cep", record.ZipCode ?? string.Empty); insert.Parameters.AddWithValue("$fonte", record.ImportSource ?? "Migração automática");
        insert.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(cancellationToken); return true;
    }

    private static string NormalizeLegacyKey(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();
    }

    public async Task<PlanCallSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
        => await _json.LoadAsync<PlanCallSettings>(_paths.PlanCallSettingsFile) ?? new PlanCallSettings();

    public Task SaveSettingsAsync(PlanCallSettings settings, CancellationToken cancellationToken = default)
        => _json.SaveAsync(_paths.PlanCallSettingsFile, settings);

    public async Task<List<PlanCallRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var records = new List<PlanCallRecord>();
        await using (var connection = OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id,posto,nome,nome_guerra,cpf,prec_cp,telefone,telefone_alt,rua,numero,complemento,bairro,cidade_uf,cep,fonte_importacao FROM plano_pessoas ORDER BY id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(new PlanCallRecord
                {
                    Id = reader.GetInt32(0),
                    Rank = reader.GetString(1),
                    Name = reader.GetString(2),
                    WarName = reader.GetString(3),
                    Cpf = reader.GetString(4),
                    PrecCp = reader.GetString(5),
                    Phone = reader.GetString(6),
                    AlternatePhone = reader.GetString(7),
                    Street = reader.GetString(8),
                    Number = reader.GetString(9),
                    Complement = reader.GetString(10),
                    District = reader.GetString(11),
                    CityState = reader.GetString(12),
                    ZipCode = reader.GetString(13),
                    ImportSource = reader.GetString(14),
                    HasOverride = true
                });
            }
        }

        // O efetivo completo sempre nasce do Listar Militares. O Plano mantém banco próprio
        // apenas para telefone/endereço importados e usa o vínculo somente para conferência.
        var military = await _military.GetAllAsync(cancellationToken);
        var matchedMilitaryIds = new HashSet<int>();
        foreach (var item in records)
        {
            var match = FindMilitaryMatch(item, military);
            if (match is null) continue;
            AttachBaseData(item, match);
            matchedMilitaryIds.Add(match.Id);
        }

        foreach (var person in military.Where(x => !matchedMilitaryIds.Contains(x.Id)))
        {
            records.Add(new PlanCallRecord
            {
                Id = 0,
                MilitaryId = person.Id,
                Rank = person.Rank,
                Name = person.Name,
                WarName = person.WarName,
                Cpf = person.Cpf,
                PrecCp = person.PrecCp,
                BasePhone = person.Phone,
                BaseEmail = person.Email,
                BaseAddress = person.Address,
                BaseZipCode = person.ZipCode,
                HasOverride = false
            });
        }

        return records
            .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }


    private static void AttachBaseData(PlanCallRecord item, MilitaryRecord match)
    {
        item.MilitaryId = match.Id;
        item.BasePhone = match.Phone;
        item.BaseEmail = match.Email;
        item.BaseAddress = match.Address;
        item.BaseZipCode = match.ZipCode;
        if (string.IsNullOrWhiteSpace(item.Rank)) item.Rank = match.Rank;
        if (string.IsNullOrWhiteSpace(item.Name)) item.Name = match.Name;
        if (string.IsNullOrWhiteSpace(item.WarName)) item.WarName = match.WarName;
        if (string.IsNullOrWhiteSpace(item.Cpf)) item.Cpf = match.Cpf;
        if (string.IsNullOrWhiteSpace(item.PrecCp)) item.PrecCp = match.PrecCp;
        item.RefreshComparison();
    }
    private static MilitaryRecord? FindMilitaryMatch(PlanCallRecord item, IReadOnlyList<MilitaryRecord> people)
    {
        var cpf = Digits(item.Cpf);
        if (cpf.Length == 11)
        {
            var byCpf = people.FirstOrDefault(x => Digits(x.Cpf) == cpf);
            if (byCpf is not null) return byCpf;
        }
        var prec = Digits(item.PrecCp);
        if (prec.Length > 0)
        {
            var byPrec = people.FirstOrDefault(x => Digits(x.PrecCp) == prec);
            if (byPrec is not null) return byPrec;
        }
        var name = Normalize(item.Name);
        if (name.Length > 0)
        {
            var exact = people.FirstOrDefault(x => Normalize(x.Name) == name);
            if (exact is not null) return exact;
            var best = people.Select(x => (Person: x, Score: Similarity(name, Normalize(x.Name))))
                .Where(x => x.Score >= 0.94).OrderByDescending(x => x.Score).FirstOrDefault();
            if (best.Person is not null) return best.Person;
        }
        return null;
    }

    public async Task SaveAsync(PlanCallRecord item, bool copyAddressToMilitary, CancellationToken cancellationToken = default)
    {
        // copyAddressToMilitary é mantido na assinatura por compatibilidade, porém o Plano
        // nunca altera endereço do Listar Militares.
        await EnsureSchemaAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(item.Name)) throw new InvalidOperationException("Informe o nome do militar no Plano de Chamada.");
        await using var connection = OpenConnection();
        if (item.Id <= 0)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO plano_pessoas(posto,nome,nome_guerra,cpf,prec_cp,telefone,telefone_alt,rua,numero,complemento,bairro,cidade_uf,cep,fonte_importacao,criado_em,atualizado_em)
                VALUES($posto,$nome,$guerra,$cpf,$prec,$tel,$alt,$rua,$num,$comp,$bairro,$cidade,$cep,$fonte,$agora,$agora);
                SELECT last_insert_rowid();
                """;
            AddRecordParameters(insert, item);
            insert.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            item.Id = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        else
        {
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE plano_pessoas SET posto=$posto,nome=$nome,nome_guerra=$guerra,cpf=$cpf,prec_cp=$prec,telefone=$tel,telefone_alt=$alt,
                    rua=$rua,numero=$num,complemento=$comp,bairro=$bairro,cidade_uf=$cidade,cep=$cep,fonte_importacao=$fonte,atualizado_em=$agora
                WHERE id=$id;
                """;
            AddRecordParameters(update, item);
            update.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", item.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void AddRecordParameters(SqliteCommand command, PlanCallRecord item)
    {
        command.Parameters.AddWithValue("$posto", MilitaryRankService.Canonicalize(item.Rank));
        command.Parameters.AddWithValue("$nome", Clean(item.Name));
        command.Parameters.AddWithValue("$guerra", Clean(item.WarName));
        command.Parameters.AddWithValue("$cpf", Digits(item.Cpf));
        command.Parameters.AddWithValue("$prec", Digits(item.PrecCp));
        command.Parameters.AddWithValue("$tel", Clean(item.Phone));
        command.Parameters.AddWithValue("$alt", Clean(item.AlternatePhone));
        command.Parameters.AddWithValue("$rua", Title(item.Street));
        command.Parameters.AddWithValue("$num", Clean(item.Number));
        command.Parameters.AddWithValue("$comp", Title(item.Complement));
        command.Parameters.AddWithValue("$bairro", Title(item.District));
        command.Parameters.AddWithValue("$cidade", NormalizeCityState(item.CityState));
        command.Parameters.AddWithValue("$cep", Digits(item.ZipCode));
        command.Parameters.AddWithValue("$fonte", Clean(item.ImportSource));
    }

    public async Task<bool> CopyMissingPhoneToMilitaryAsync(PlanCallRecord item, CancellationToken cancellationToken = default)
    {
        if (!item.CanCopyPhoneToMilitary) return false;
        await _military.UpdateContactAsync(item.MilitaryId, item.Phone, item.BaseEmail, cancellationToken);
        item.BasePhone = item.Phone;
        item.RefreshComparison();
        return true;
    }

    public async Task<int> CopyAllMissingPhonesToMilitaryAsync(IEnumerable<PlanCallRecord> records, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var item in records.Where(x => x.CanCopyPhoneToMilitary))
            if (await CopyMissingPhoneToMilitaryAsync(item, cancellationToken)) count++;
        return count;
    }

    public async Task ClearOverrideAsync(int planId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await CreateRestorePointAsync("Antes de excluir registro do Plano", cancellationToken);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM plano_pessoas WHERE id=$id;";
        command.Parameters.AddWithValue("$id", planId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<PlanCallImportRow>> ReadImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var table = await SpreadsheetService.ReadTabularFileAsync(path, cancellationToken);
        if (table.Count == 0) return [];
        var headerIndex = FindHeaderRow(table);
        var headers = table[headerIndex].Select(NormalizeHeader).ToList();
        var result = new List<PlanCallImportRow>();
        for (var r = headerIndex + 1; r < table.Count; r++)
        {
            var row = table[r];
            string V(params string[] names)
            {
                foreach (var name in names)
                {
                    var idx = headers.FindIndex(x => x == NormalizeHeader(name));
                    if (idx >= 0 && idx < row.Count && !string.IsNullOrWhiteSpace(row[idx])) return row[idx].Trim();
                }
                return string.Empty;
            }
            var rawAddress = V("endereco", "endereço", "logradouro");
            var parsed = ParseAddress(rawAddress);
            var item = new PlanCallImportRow
            {
                SourceRow = r + 1,
                Rank = V("posto", "posto graduacao", "p g", "pg"),
                Name = V("nome", "nome completo", "militar"),
                WarName = V("nome de guerra", "guerra"),
                Cpf = V("cpf"), PrecCp = V("prec cp", "prec"),
                Phone = V("telefone", "celular", "fone"), AlternatePhone = V("telefone alternativo", "fone 2", "telefone 2"),
                Street = First(V("rua", "avenida"), parsed.Street), Number = First(V("numero", "número", "n"), parsed.Number),
                Complement = First(V("complemento"), parsed.Complement), District = First(V("bairro"), parsed.District),
                CityState = First(V("cidade uf", "cidade", "municipio", "município"), parsed.CityState),
                ZipCode = V("cep"), RawAddress = rawAddress
            };
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            result.Add(item);
        }
        return result;
    }

    public List<PlanCallImportMatch> MatchImports(IReadOnlyList<PlanCallImportRow> imports, IReadOnlyList<PlanCallRecord> current)
    {
        var result = new List<PlanCallImportMatch>();
        foreach (var imported in imports)
        {
            var match = FindPlanMatch(imported, current, out var kind, out var confidence);
            var importedAddress = FormatAddress(imported.Street, imported.Number, imported.Complement, imported.District, imported.CityState, imported.RawAddress);
            var change = match is null || !match.HasPlanAddress ? "Adicionar dados do Plano" : AddressEquivalent(match.PlanAddress, importedAddress) ? "Dados/telefone" : "Endereço alterado";
            result.Add(new PlanCallImportMatch { Imported = imported, Current = match, MatchKind = kind, ChangeKind = change, Confidence = confidence, Apply = true });
        }
        return result;
    }

    private static PlanCallRecord? FindPlanMatch(PlanCallImportRow imported, IReadOnlyList<PlanCallRecord> current, out string kind, out double confidence)
    {
        var cpf = Digits(imported.Cpf);
        if (cpf.Length == 11)
        {
            var found = current.FirstOrDefault(x => Digits(x.Cpf) == cpf);
            if (found is not null) { kind = "CPF"; confidence = 1; return found; }
        }
        var prec = Digits(imported.PrecCp);
        if (prec.Length > 0)
        {
            var found = current.FirstOrDefault(x => Digits(x.PrecCp) == prec);
            if (found is not null) { kind = "PREC-CP"; confidence = 1; return found; }
        }
        var name = Normalize(imported.Name);
        var exact = current.FirstOrDefault(x => Normalize(x.Name) == name);
        if (exact is not null) { kind = "Nome"; confidence = 1; return exact; }
        var best = current.Select(x => (Item: x, Score: Similarity(name, Normalize(x.Name)))).OrderByDescending(x => x.Score).FirstOrDefault();
        if (best.Item is not null && best.Score >= 0.92) { kind = "Nome semelhante"; confidence = best.Score; return best.Item; }
        kind = "Novo no Plano"; confidence = 0; return null;
    }

    public async Task<int> ApplyImportAsync(IEnumerable<PlanCallImportMatch> matches, string source, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var selected = matches.Where(x => x.Apply).ToList();
        if (selected.Count == 0) return 0;
        await CreateRestorePointAsync($"Antes da importação — {Path.GetFileName(source)}", cancellationToken);
        var count = 0;
        foreach (var match in selected)
        {
            var imported = match.Imported;
            var item = match.Current ?? new PlanCallRecord();
            item.Rank = First(imported.Rank, item.Rank);
            item.Name = First(imported.Name, item.Name);
            item.WarName = First(imported.WarName, item.WarName);
            item.Cpf = First(imported.Cpf, item.Cpf);
            item.PrecCp = First(imported.PrecCp, item.PrecCp);
            item.Phone = First(imported.Phone, item.Phone);
            item.AlternatePhone = First(imported.AlternatePhone, item.AlternatePhone);
            item.Street = First(imported.Street, item.Street);
            item.Number = First(imported.Number, item.Number);
            item.Complement = First(imported.Complement, item.Complement);
            item.District = First(imported.District, item.District);
            item.CityState = First(imported.CityState, item.CityState);
            item.ZipCode = First(imported.ZipCode, item.ZipCode);
            item.ImportSource = Path.GetFileName(source);
            await SaveAsync(item, false, cancellationToken);
            count++;
        }
        return count;
    }

    public async Task<int> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var records = await LoadRawAsync(cancellationToken);
        var json = JsonSerializer.Serialize(records);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO plano_restore(criado_em,descricao,snapshot_json) VALUES($at,$desc,$json); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$at", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$desc", description ?? string.Empty);
        command.Parameters.AddWithValue("$json", json);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task<List<PlanCallRecord>> LoadRawAsync(CancellationToken cancellationToken)
    {
        var result = new List<PlanCallRecord>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,posto,nome,nome_guerra,cpf,prec_cp,telefone,telefone_alt,rua,numero,complemento,bairro,cidade_uf,cep,fonte_importacao FROM plano_pessoas ORDER BY id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new PlanCallRecord
        {
            Id = reader.GetInt32(0), Rank = reader.GetString(1), Name = reader.GetString(2), WarName = reader.GetString(3),
            Cpf = reader.GetString(4), PrecCp = reader.GetString(5), Phone = reader.GetString(6), AlternatePhone = reader.GetString(7),
            Street = reader.GetString(8), Number = reader.GetString(9), Complement = reader.GetString(10), District = reader.GetString(11),
            CityState = reader.GetString(12), ZipCode = reader.GetString(13), ImportSource = reader.GetString(14)
        });
        return result;
    }

    public async Task<List<PlanCallRestorePoint>> ListRestorePointsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<PlanCallRestorePoint>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,criado_em,descricao FROM plano_restore ORDER BY id DESC LIMIT 40;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTime.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
            result.Add(new PlanCallRestorePoint { Id = reader.GetInt32(0), CreatedAt = at, Description = reader.GetString(2) });
        }
        return result;
    }

    public async Task<int> RestoreAsync(int pointId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await CreateRestorePointAsync($"Backup automático antes de restaurar #{pointId}", cancellationToken);
        string? json;
        await using (var connection = OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT snapshot_json FROM plano_restore WHERE id=$id;";
            command.Parameters.AddWithValue("$id", pointId);
            json = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("Ponto de restauração não encontrado.");
        var records = JsonSerializer.Deserialize<List<PlanCallRecord>>(json) ?? [];
        await using var conn = OpenConnection();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
        await using (var clear = conn.CreateCommand()) { clear.Transaction = tx; clear.CommandText = "DELETE FROM plano_pessoas;"; await clear.ExecuteNonQueryAsync(cancellationToken); }
        foreach (var item in records)
        {
            await using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO plano_pessoas(id,posto,nome,nome_guerra,cpf,prec_cp,telefone,telefone_alt,rua,numero,complemento,bairro,cidade_uf,cep,fonte_importacao,criado_em,atualizado_em)
                VALUES($id,$posto,$nome,$guerra,$cpf,$prec,$tel,$alt,$rua,$num,$comp,$bairro,$cidade,$cep,$fonte,$agora,$agora);
                """;
            AddRecordParameters(insert, item);
            insert.Parameters.AddWithValue("$id", item.Id);
            insert.Parameters.AddWithValue("$agora", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return records.Count;
    }

    public async Task<ViaCepAddress?> LookupZipCodeAsync(string zipCode, CancellationToken cancellationToken = default)
    {
        var cep = Digits(zipCode);
        if (cep.Length != 8) throw new InvalidOperationException("Informe um CEP com 8 dígitos.");
        using var response = await Http.GetAsync($"https://viacep.com.br/ws/{cep}/json/", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("erro", out _)) return null;
        return ParseViaCep(doc.RootElement);
    }

    public async Task<List<ViaCepAddress>> LookupAddressAsync(string street, string cityState, CancellationToken cancellationToken = default)
    {
        var parts = cityState.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new InvalidOperationException("Informe Cidade/UF no formato Belo Horizonte/MG.");
        var city = Uri.EscapeDataString(parts[0]); var state = Uri.EscapeDataString(parts[^1]); var road = Uri.EscapeDataString(street.Trim());
        using var response = await Http.GetAsync($"https://viacep.com.br/ws/{state}/{city}/{road}/json/", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray().Take(30).Select(ParseViaCep).ToList();
    }

    private static ViaCepAddress ParseViaCep(JsonElement e) => new()
    {
        ZipCode = e.TryGetProperty("cep", out var cep) ? cep.GetString() ?? string.Empty : string.Empty,
        Street = e.TryGetProperty("logradouro", out var road) ? road.GetString() ?? string.Empty : string.Empty,
        District = e.TryGetProperty("bairro", out var district) ? district.GetString() ?? string.Empty : string.Empty,
        City = e.TryGetProperty("localidade", out var city) ? city.GetString() ?? string.Empty : string.Empty,
        State = e.TryGetProperty("uf", out var state) ? state.GetString() ?? string.Empty : string.Empty
    };

    public async Task ExportExcelAsync(string path, IReadOnlyList<PlanCallRecord> items, bool groupByRegion, CancellationToken cancellationToken = default)
    {
        var rows = BuildSpreadsheetRows(items, groupByRegion);
        await SpreadsheetService.WriteXlsxAsync(path, "Plano de Chamada",
            ["P/G", "Nome", "Telefone", "Telefone alternativo", "Endereço", "CEP", "Região", "Conferência com Listar"],
            [12, 34, 18, 18, 48, 13, 20, 22], rows, cancellationToken);
    }

    private static List<SpreadsheetRow> BuildSpreadsheetRows(IReadOnlyList<PlanCallRecord> items, bool groupByRegion)
    {
        var result = new List<SpreadsheetRow>();
        IEnumerable<IGrouping<string, PlanCallRecord>> groups = groupByRegion
            ? items.GroupBy(x => string.IsNullOrWhiteSpace(x.Region) ? "Sem região" : x.Region).OrderBy(x => x.Key)
            : new[] { items.GroupBy(_ => string.Empty).Single() };
        foreach (var group in groups)
        {
            if (groupByRegion) result.Add(new SpreadsheetRow { IsGroup = true, Cells = [new SpreadsheetCell { Text = group.Key }] });
            foreach (var item in group.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var segments = NameHighlightHelper.BuildSegments(item.Name.ToUpper(PtBr), item.WarName.ToUpper(PtBr));
                result.Add(new SpreadsheetRow { Cells =
                [
                    new() { Text = item.ShortRank },
                    new() { Text = item.Name.ToUpper(PtBr), Runs = segments.Select(x => new SpreadsheetRun { Text = x.Text, Bold = x.IsBold }).ToList() },
                    new() { Text = item.Phone }, new() { Text = item.AlternatePhone }, new() { Text = item.EffectiveAddress },
                    new() { Text = FormatZipCode(item.ZipCode) }, new() { Text = item.Region }, new() { Text = item.DifferenceStatus }
                ]});
            }
        }
        return result;
    }

    public async Task ExportOdtAsync(string path, IReadOnlyList<PlanCallRecord> items, bool groupByRegion, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path) ?? _paths.PlanCallOutputDirectory;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, true);
                var mime = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
                await using (var writer = new StreamWriter(mime.Open(), new UTF8Encoding(false))) await writer.WriteAsync("application/vnd.oasis.opendocument.text");
                await WriteEntryAsync(archive, "META-INF/manifest.xml", OdtManifest, cancellationToken);
                await WriteEntryAsync(archive, "content.xml", BuildOdtContent(items, groupByRegion), cancellationToken);
                await WriteEntryAsync(archive, "styles.xml", OdtStyles, cancellationToken);
            }
            File.Move(temp, path, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    public async Task ExportPdfAsync(string path, IReadOnlyList<PlanCallRecord> items, bool groupByRegion, CancellationToken cancellationToken = default)
    {
        var tempOdt = Path.Combine(Path.GetTempPath(), $"plano_chamada_{Guid.NewGuid():N}.odt");
        try
        {
            await ExportOdtAsync(tempOdt, items, groupByRegion, cancellationToken);
            var soffice = FindLibreOffice() ?? throw new InvalidOperationException("LibreOffice não encontrado. Instale-o para gerar PDF ou use Excel/ODT.");
            var output = Path.GetDirectoryName(path) ?? _paths.PlanCallOutputDirectory;
            Directory.CreateDirectory(output);
            var psi = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "--headless", "--nologo", "--nofirststartwizard", "--convert-to", "pdf", "--outdir", output, tempOdt }) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o LibreOffice.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException((await process.StandardError.ReadToEndAsync()).Trim());
            var generated = Path.Combine(output, Path.GetFileNameWithoutExtension(tempOdt) + ".pdf");
            if (!File.Exists(generated)) throw new FileNotFoundException("O PDF não foi criado.", generated);
            File.Move(generated, path, true);
        }
        finally { try { if (File.Exists(tempOdt)) File.Delete(tempOdt); } catch { } }
    }

    private static string BuildOdtContent(IReadOnlyList<PlanCallRecord> items, bool group)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" office:version=\"1.2\"><office:body><office:text>");
        sb.Append("<text:p text:style-name=\"Title\">PLANO DE CHAMADA</text:p>");
        IEnumerable<IGrouping<string, PlanCallRecord>> groups = group ? items.GroupBy(x => x.Region).OrderBy(x => x.Key) : new[] { items.GroupBy(_ => string.Empty).Single() };
        foreach (var g in groups)
        {
            if (group) sb.Append("<text:h text:outline-level=\"1\">").Append(Xml(g.Key)).Append("</text:h>");
            sb.Append("<table:table table:name=\"Plano\"><table:table-row>");
            foreach (var h in new[] { "P/G", "Nome", "Telefone", "Endereço", "CEP", "Conferência" }) sb.Append("<table:table-cell><text:p><text:span text:style-name=\"Bold\">").Append(Xml(h)).Append("</text:span></text:p></table:table-cell>");
            sb.Append("</table:table-row>");
            foreach (var item in g.OrderBy(x => MilitaryRankService.GetOrder(x.Rank)).ThenBy(x => x.Name))
            {
                sb.Append("<table:table-row>");
                OdtCell(sb, item.ShortRank); OdtNameCell(sb, item); OdtCell(sb, item.Phone); OdtCell(sb, item.EffectiveAddress); OdtCell(sb, FormatZipCode(item.ZipCode)); OdtCell(sb, item.DifferenceStatus);
                sb.Append("</table:table-row>");
            }
            sb.Append("</table:table>");
        }
        sb.Append("</office:text></office:body></office:document-content>");
        return sb.ToString();
    }

    private static void OdtCell(StringBuilder sb, string value) => sb.Append("<table:table-cell><text:p>").Append(Xml(value)).Append("</text:p></table:table-cell>");
    private static void OdtNameCell(StringBuilder sb, PlanCallRecord item)
    {
        sb.Append("<table:table-cell><text:p>");
        foreach (var segment in NameHighlightHelper.BuildSegments(item.Name.ToUpper(PtBr), item.WarName.ToUpper(PtBr)))
        {
            if (segment.IsBold) sb.Append("<text:span text:style-name=\"Bold\">");
            sb.Append(Xml(segment.Text));
            if (segment.IsBold) sb.Append("</text:span>");
        }
        sb.Append("</text:p></table:table-cell>");
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static string? FindLibreOffice() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe")
    }.FirstOrDefault(File.Exists);

    public static AddressParts ParseAddress(string? address)
    {
        var raw = Clean(address);
        if (string.IsNullOrWhiteSpace(raw)) return new AddressParts();
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var first = parts.ElementAtOrDefault(0) ?? string.Empty;
        var number = string.Empty;
        var numberMatch = Regex.Match(first, @"^(.*?)(?:\s*[,-]?\s+(\d+[A-Za-z]?|S/?N))$", RegexOptions.IgnoreCase);
        if (numberMatch.Success) { first = numberMatch.Groups[1].Value.Trim(); number = numberMatch.Groups[2].Value.Trim(); }
        return new AddressParts
        {
            Street = first,
            Number = number,
            Complement = parts.Count >= 5 ? parts[2] : string.Empty,
            District = parts.Count >= 3 ? parts[^2] : parts.ElementAtOrDefault(1) ?? string.Empty,
            CityState = parts.Count >= 2 ? parts[^1] : string.Empty
        };
    }

    public static AddressParts CanonicalizeAddress(string street, string number, string complement, string district, string cityState) => new()
    {
        Street = Title(street), Number = Clean(number), Complement = Title(complement), District = Title(district), CityState = NormalizeCityState(cityState)
    };

    public static string FormatAddress(string? street, string? number, string? complement, string? district, string? cityState, string? fallback)
    {
        var parts = new List<string>();
        var first = Clean(street);
        if (!string.IsNullOrWhiteSpace(number)) first = string.IsNullOrWhiteSpace(first) ? Clean(number) : $"{first}, {Clean(number)}";
        if (!string.IsNullOrWhiteSpace(first)) parts.Add(first);
        if (!string.IsNullOrWhiteSpace(complement)) parts.Add(Clean(complement));
        if (!string.IsNullOrWhiteSpace(district)) parts.Add(Clean(district));
        if (!string.IsNullOrWhiteSpace(cityState)) parts.Add(NormalizeCityState(cityState));
        return parts.Count == 0 ? Clean(fallback) : string.Join(" - ", parts);
    }

    public static bool AddressEquivalent(string? a, string? b)
    {
        var na = NormalizeAddress(a); var nb = NormalizeAddress(b);
        if (string.IsNullOrWhiteSpace(na) || string.IsNullOrWhiteSpace(nb)) return false;
        return na == nb || Similarity(na, nb) >= 0.91;
    }

    public static bool PhoneEquivalent(string? a, string? b)
    {
        var da = Digits(a); var db = Digits(b);
        if (da.Length == 0 || db.Length == 0) return false;
        return da == db || (da.Length >= 8 && db.Length >= 8 && da[^8..] == db[^8..]);
    }

    public static string FormatZipCode(string? value)
    {
        var digits = Digits(value);
        return digits.Length == 8 ? $"{digits[..5]}-{digits[5..]}" : Clean(value);
    }

    public static string RegionFor(string? address, string? cityState)
    {
        var text = Normalize($"{address} {cityState}");
        if (text.Contains("belo horizonte")) return "Belo Horizonte";
        foreach (var knownCity in new[] { "contagem", "betim", "santa luzia", "ribeirao das neves", "sabara", "ibirite", "nova lima" })
            if (text.Contains(knownCity)) return Title(knownCity);
        var cityName = Clean(cityState).Split('/', StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(cityName) ? "Outras regiões" : Title(cityName);
    }

    private static int FindHeaderRow(IReadOnlyList<List<string>> table)
    {
        for (var i = 0; i < Math.Min(20, table.Count); i++)
        {
            var normalized = table[i].Select(NormalizeHeader).ToList();
            if (normalized.Any(x => x is "nome" or "nome completo" or "militar")) return i;
        }
        return 0;
    }

    private static string NormalizeHeader(string? value) => Normalize(value).Replace("/", " ").Trim();
    private static string NormalizeAddress(string? value) => Regex.Replace(Normalize(value), @"\b(rua|r|avenida|av|numero|n)\b", " ").Replace(" s n ", " ").Trim();
    private static string Normalize(string? value)
    {
        var form = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var noAccents = new string(form.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(noAccents.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    }

    private static double Similarity(string a, string b)
    {
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var costs = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) costs[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var previous = costs[0]; costs[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var old = costs[j];
                costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = old;
            }
        }
        return 1d - (double)costs[b.Length] / Math.Max(a.Length, b.Length);
    }

    private static string Clean(string? value) => Regex.Replace(value?.Trim() ?? string.Empty, @"\s+", " ");
    private static string First(params string?[] values) => values.Select(Clean).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    public static string Digits(string? value) => Regex.Replace(value ?? string.Empty, @"\D+", string.Empty);
    private static string Title(string? value) => PtBr.TextInfo.ToTitleCase(Clean(value).ToLower(PtBr));
    private static string NormalizeCityState(string? value)
    {
        var text = Clean(value).Replace(" - ", "/");
        var parts = text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{Title(parts[0])}/{parts[^1].ToUpperInvariant()}" : Title(text);
    }
    private static string Xml(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

    private const string OdtManifest = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/><manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>";
    private const string OdtStyles = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-styles xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" office:version=\"1.2\"><office:styles><style:style style:name=\"Bold\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"/></style:style><style:style style:name=\"Title\" style:family=\"paragraph\"><style:text-properties fo:font-size=\"16pt\" fo:font-weight=\"bold\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\"/></style:style></office:styles></office:document-styles>";
}
