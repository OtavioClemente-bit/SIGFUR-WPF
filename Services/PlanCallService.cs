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
/// Plano de Chamada com banco próprio. A Carteira e o Auxílio-Transporte são usados
/// como fontes de conferência e somente campos ausentes podem ser completados no cadastro.
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
        var transportAddresses = await LoadTransportAddressesAsync(cancellationToken);
        var matchedMilitaryIds = new HashSet<int>();
        foreach (var item in records)
        {
            var match = FindMilitaryMatch(item, military);
            if (match is null) continue;
            AttachBaseData(item, match, transportAddresses.GetValueOrDefault(match.Id));
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
                TransportAddress = transportAddresses.GetValueOrDefault(person.Id) ?? string.Empty,
                HasOverride = false
            });
        }

        return records
            .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }


    private static void AttachBaseData(PlanCallRecord item, MilitaryRecord match, string? transportAddress)
    {
        item.MilitaryId = match.Id;
        item.BasePhone = match.Phone;
        item.BaseEmail = match.Email;
        item.BaseAddress = match.Address;
        item.BaseZipCode = match.ZipCode;
        item.TransportAddress = transportAddress ?? string.Empty;
        if (string.IsNullOrWhiteSpace(item.Rank)) item.Rank = match.Rank;
        if (string.IsNullOrWhiteSpace(item.Name)) item.Name = match.Name;
        if (string.IsNullOrWhiteSpace(item.WarName)) item.WarName = match.WarName;
        if (string.IsNullOrWhiteSpace(item.Cpf)) item.Cpf = match.Cpf;
        if (string.IsNullOrWhiteSpace(item.PrecCp)) item.PrecCp = match.PrecCp;
        item.RefreshComparison();
    }

    private async Task<Dictionary<int, string>> LoadTransportAddressesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        if (!File.Exists(_paths.TransportRoutesDatabaseFile)) return result;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _paths.TransportRoutesDatabaseFile,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 5
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);
            await using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='rotas_militar' LIMIT 1;";
                if (await exists.ExecuteScalarAsync(cancellationToken) is null) return result;
            }
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT militar_id,TRIM(COALESCE(origem,'')) FROM rotas_militar WHERE TRIM(COALESCE(origem,''))<>'';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result[reader.GetInt32(0)] = Clean(reader.GetString(1));
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Plano de Chamada: não foi possível consultar os endereços do Auxílio-Transporte.", ex);
        }
        return result;
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
        command.Parameters.AddWithValue("$tel", FormatPhone(item.Phone));
        command.Parameters.AddWithValue("$alt", FormatPhone(item.AlternatePhone));
        command.Parameters.AddWithValue("$rua", CanonicalizeStreet(item.Street));
        command.Parameters.AddWithValue("$num", CanonicalizeNumber(item.Number));
        command.Parameters.AddWithValue("$comp", CanonicalizeComplement(item.Complement));
        command.Parameters.AddWithValue("$bairro", CanonicalizeDistrict(item.District));
        command.Parameters.AddWithValue("$cidade", NormalizeCityState(item.CityState));
        command.Parameters.AddWithValue("$cep", Digits(item.ZipCode));
        command.Parameters.AddWithValue("$fonte", Clean(item.ImportSource));
    }

    public async Task<bool> CopyMissingPhoneToMilitaryAsync(PlanCallRecord item, CancellationToken cancellationToken = default)
    {
        if (!item.CanCopyPhoneToMilitary) return false;
        var phone = FormatPhone(item.Phone);
        await _military.UpdateContactAsync(item.MilitaryId, phone, item.BaseEmail, cancellationToken);
        item.BasePhone = phone;
        item.RefreshComparison();
        return true;
    }

    public async Task<bool> CopyMissingAddressToMilitaryAsync(PlanCallRecord item, CancellationToken cancellationToken = default)
    {
        if (!item.CanCopyAddressToMilitary) return false;
        var address = FormatAddress(item.Street, item.Number, item.Complement, item.District, item.CityState, string.Empty);
        if (!await _military.UpdateAddressIfMissingAsync(item.MilitaryId, address, item.ZipCode, cancellationToken)) return false;
        item.BaseAddress = address;
        item.BaseZipCode = FormatZipCode(item.ZipCode);
        item.RefreshComparison();
        return true;
    }

    public async Task<PlanCallCopyResult> CopyMissingDataToMilitaryAsync(PlanCallRecord item,
        CancellationToken cancellationToken = default)
    {
        var result = new PlanCallCopyResult();
        if (await CopyMissingPhoneToMilitaryAsync(item, cancellationToken)) result.Phones++;
        if (await CopyMissingAddressToMilitaryAsync(item, cancellationToken)) result.Addresses++;
        return result;
    }

    public async Task<int> CopyAllMissingPhonesToMilitaryAsync(IEnumerable<PlanCallRecord> records, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var item in records.Where(x => x.CanCopyPhoneToMilitary))
            if (await CopyMissingPhoneToMilitaryAsync(item, cancellationToken)) count++;
        return count;
    }

    public async Task<PlanCallCopyResult> CopyAllMissingDataToMilitaryAsync(IEnumerable<PlanCallRecord> records,
        CancellationToken cancellationToken = default)
    {
        var result = new PlanCallCopyResult();
        foreach (var item in records.Where(x => x.CanCompleteMilitary))
        {
            var copied = await CopyMissingDataToMilitaryAsync(item, cancellationToken);
            result.Phones += copied.Phones;
            result.Addresses += copied.Addresses;
        }
        return result;
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
            var rawAddress = ImportValue(V("endereco", "endereço", "endereço residencial", "logradouro"));
            var parsed = ParseAddress(rawAddress);
            var street = First(ImportValue(V("end rua av", "endereco rua av", "endereco rua avenida", "rua avenida", "rua", "avenida", "logradouro")), parsed.Street);
            var number = First(ImportValue(V("numero", "número", "nr", "n")), parsed.Number);
            var complement = First(ImportValue(V("complemento", "compl")), parsed.Complement);
            var district = First(ImportValue(V("bairro")), parsed.District);
            var cityState = First(ImportValue(V("cidade uf", "cidade estado", "cidade", "municipio", "município")), parsed.CityState);
            var canonicalAddress = CanonicalizeAddress(street, number, complement, district, cityState);
            var item = new PlanCallImportRow
            {
                SourceRow = r + 1,
                Rank = MilitaryRankService.Canonicalize(ImportValue(V("posto", "posto graduacao", "p g", "pg"))),
                Name = ImportValue(V("nome", "nome completo", "militar")),
                WarName = ImportValue(V("nome de guerra", "guerra")),
                Cpf = ImportValue(V("cpf")), PrecCp = ImportValue(V("prec cp", "prec")),
                Phone = FormatPhone(ImportValue(V("tel pessoal", "telefone pessoal", "telefone", "celular", "fone", "whatsapp"))),
                AlternatePhone = FormatPhone(ImportValue(V("tel alternativo", "telefone alternativo", "telefone alt", "tel alt", "fone 2", "telefone 2"))),
                Street = canonicalAddress.Street, Number = canonicalAddress.Number,
                Complement = canonicalAddress.Complement, District = canonicalAddress.District,
                CityState = canonicalAddress.CityState,
                ZipCode = FormatZipCode(ImportValue(V("cep"))), RawAddress = rawAddress
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
            string change;
            if (kind == "Revisar pessoa") change = "Conferir vínculo";
            else if (match is null || !match.HasPlanAddress) change = "Adicionar ao Plano";
            else
            {
                var addressStatus = CompareAddresses(match.PlanAddress, importedAddress);
                var phoneChanged = !string.IsNullOrWhiteSpace(imported.Phone)
                                   && !PhoneEquivalent(match.Phone, imported.Phone);
                change = addressStatus switch
                {
                    "DIVERGENTE" => phoneChanged ? "Atualizar endereço e telefone" : "Atualizar endereço",
                    "COMPATÍVEL" => phoneChanged ? "Padronizar e atualizar telefone" : "Padronizar formato",
                    _ when phoneChanged => "Atualizar telefone",
                    _ => "Sem mudança relevante"
                };
            }
            result.Add(new PlanCallImportMatch
            {
                Imported = imported,
                Current = match,
                MatchKind = kind,
                ChangeKind = change,
                Confidence = confidence,
                Apply = kind != "Revisar pessoa"
            });
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
        var warName = Normalize(imported.WarName);
        if (warName.Length >= 3)
        {
            var byWarName = current.Where(x => Normalize(x.WarName) == warName).ToList();
            if (byWarName.Count == 1)
            {
                kind = "Nome de guerra";
                confidence = 0.98;
                return byWarName[0];
            }
        }
        var ranked = current.Select(x => (Item: x, Score: Similarity(name, Normalize(x.Name))))
            .OrderByDescending(x => x.Score).Take(2).ToList();
        var best = ranked.FirstOrDefault();
        var runnerUp = ranked.Skip(1).FirstOrDefault();
        if (best.Item is not null && best.Score >= 0.93 && (runnerUp.Item is null || best.Score - runnerUp.Score >= 0.04))
        {
            kind = "Nome semelhante";
            confidence = best.Score;
            return best.Item;
        }
        if (best.Item is not null && best.Score >= 0.86)
        {
            kind = "Revisar pessoa";
            confidence = best.Score;
            return null;
        }
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
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Não foi possível consultar esse CEP no ViaCEP. Confira os números ou tente novamente.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("erro", out _)) return null;
        return ParseViaCep(doc.RootElement);
    }

    public async Task<List<ViaCepAddress>> LookupAddressAsync(string street, string cityState, CancellationToken cancellationToken = default)
    {
        var lookupStreet = NormalizeStreetForLookup(street);
        var lookupCityState = NormalizeCityStateForLookup(cityState);
        var parts = lookupCityState.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new InvalidOperationException("Informe Cidade/UF no formato Belo Horizonte/MG.");
        if (lookupStreet.Length < 3) throw new InvalidOperationException("Informe pelo menos 3 letras do logradouro para buscar o CEP.");

        var city = Uri.EscapeDataString(parts[0]);
        var state = Uri.EscapeDataString(parts[^1].ToUpperInvariant());
        var road = Uri.EscapeDataString(lookupStreet);

        using var response = await Http.GetAsync($"https://viacep.com.br/ws/{state}/{city}/{road}/json/", cancellationToken);
        if (!response.IsSuccessStatusCode) return [];

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray()
            .Take(30)
            .Select(ParseViaCep)
            .Where(x => !string.IsNullOrWhiteSpace(x.ZipCode))
            .ToList();
    }

    public static string NormalizeStreetForLookup(string? street)
    {
        var text = Clean(street);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Remove número/complemento comum antes de consultar o ViaCEP.
        // Ex.: "Rua Flor de Maio, Nº 542, Casa" -> "Rua Flor de Maio".
        text = Regex.Split(text, @"\s*,\s*(?:N[ºo°]?\s*)?(?:\d+[A-Za-z]?|S/?N)\b", RegexOptions.IgnoreCase).FirstOrDefault() ?? text;
        text = Regex.Replace(text, @"\b(?:N[ºo°]?\s*)?(?:\d+[A-Za-z]?|S/?N)\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim(' ', ',', '-', '.');
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static string NormalizeCityStateForLookup(string? cityState)
    {
        var raw = Clean(cityState);
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var matches = Regex.Matches(raw, @"(?<city>[A-Za-zÀ-ÿ][A-Za-zÀ-ÿ0-9 .,'’`´-]{1,90})\s*/\s*(?<uf>[A-Za-z]{2})\b", RegexOptions.IgnoreCase);
        if (matches.Count == 0) return raw;

        var match = matches[^1];
        var city = match.Groups["city"].Value.Trim();
        // Quando o cadastro antigo vem como "Casa. Bairro X. Belo Horizonte/MG",
        // pega só o último pedaço antes da UF.
        city = Regex.Split(city, @"\s+-\s+|[,.]|\bBairro\b", RegexOptions.IgnoreCase)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .LastOrDefault() ?? city;
        city = Regex.Replace(city, @"\s+", " ").Trim(' ', '-', ',', '.');
        var uf = match.Groups["uf"].Value.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(city) ? raw : $"{city}/{uf}";
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
            ["Nº", "P/G", "Nome completo", "Telefone", "Telefone alternativo", "Endereço do Plano de Chamada", "Endereço da Carteira / Auxílio-Transporte", "CEP", "Região", "Conferência do endereço"],
            [7, 12, 34, 18, 20, 48, 52, 13, 21, 24], rows, cancellationToken);
    }

    private static List<SpreadsheetRow> BuildSpreadsheetRows(IReadOnlyList<PlanCallRecord> items, bool groupByRegion)
    {
        var result = new List<SpreadsheetRow>();
        var ordered = OrderForProfessionalRelation(items).ToList();
        IEnumerable<IGrouping<string, PlanCallRecord>> groups = groupByRegion
            ? ordered.GroupBy(x => string.IsNullOrWhiteSpace(x.Region) ? "Região não identificada" : x.Region)
            : new[] { ordered.GroupBy(_ => string.Empty).Single() };
        var position = 1;
        foreach (var group in groups)
        {
            if (groupByRegion) result.Add(new SpreadsheetRow { IsGroup = true, Cells = [new SpreadsheetCell { Text = $"REGIÃO • {group.Key.ToUpper(PtBr)} • {group.Count()} MILITAR(ES)" }] });
            foreach (var item in group)
            {
                var segments = NameHighlightHelper.BuildSegments(item.Name.ToUpper(PtBr), item.WarName.ToUpper(PtBr));
                result.Add(new SpreadsheetRow { Cells =
                [
                    new() { Text = (position++).ToString(CultureInfo.InvariantCulture) }, new() { Text = item.ShortRank },
                    new() { Text = item.Name.ToUpper(PtBr), Runs = segments.Select(x => new SpreadsheetRun { Text = x.Text, Bold = x.IsBold }).ToList() },
                    new() { Text = item.EffectivePhone }, new() { Text = item.AlternatePhone }, new() { Text = item.PlanAddress },
                    new() { Text = item.ReferenceAddress }, new() { Text = FormatZipCode(item.EffectiveZipCode) }, new() { Text = item.Region },
                    new() { Text = item.DifferenceStatus }
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
        sb.Append("<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" office:version=\"1.2\"><office:body><office:text>");
        sb.Append("<text:p text:style-name=\"Title\">PLANO DE CHAMADA</text:p>");
        sb.Append("<text:p text:style-name=\"Subtitle\">RELAÇÃO OPERACIONAL POR REGIÃO • MAIS DISTANTES PRIMEIRO</text:p>");
        sb.Append("<text:p text:style-name=\"Meta\">Efetivo: ").Append(items.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" militar(es) • Emitido em ").Append(Xml(DateTime.Now.ToString("dd/MM/yyyy 'as' HH:mm", PtBr))).Append("</text:p>");
        var ordered = OrderForProfessionalRelation(items).ToList();
        IEnumerable<IGrouping<string, PlanCallRecord>> groups = group
            ? ordered.GroupBy(x => string.IsNullOrWhiteSpace(x.Region) ? "Região não identificada" : x.Region)
            : new[] { ordered.GroupBy(_ => string.Empty).Single() };
        var position = 1;
        var tableIndex = 1;
        foreach (var g in groups)
        {
            if (group) sb.Append("<text:p text:style-name=\"RegionHeading\">REGIÃO • ").Append(Xml(g.Key.ToUpper(PtBr))).Append(" • ").Append(g.Count().ToString(CultureInfo.InvariantCulture)).Append(" MILITAR(ES)</text:p>");
            sb.Append("<table:table table:name=\"Regiao").Append(tableIndex++.ToString(CultureInfo.InvariantCulture)).Append("\" table:style-name=\"PlanTable\">");
            foreach (var style in new[] { "ColNumber", "ColRank", "ColName", "ColPhone", "ColPlanAddress", "ColReferenceAddress", "ColStatus" })
                sb.Append("<table:table-column table:style-name=\"").Append(style).Append("\"/>");
            sb.Append("<table:table-row>");
            foreach (var h in new[] { "Nº", "P/G", "NOME COMPLETO", "CONTATO", "ENDEREÇO DO PLANO", "CARTEIRA / AUXÍLIO-TRANSPORTE", "CONFERÊNCIA" })
                sb.Append("<table:table-cell table:style-name=\"HeaderCell\"><text:p text:style-name=\"HeaderText\">").Append(Xml(h)).Append("</text:p></table:table-cell>");
            sb.Append("</table:table-row>");
            foreach (var item in g)
            {
                sb.Append("<table:table-row>");
                OdtCell(sb, (position++).ToString(CultureInfo.InvariantCulture));
                OdtCell(sb, item.ShortRank);
                OdtNameCell(sb, item);
                OdtCell(sb, ContactFor(item));
                OdtCell(sb, item.PlanAddress);
                OdtCell(sb, item.ReferenceAddress);
                OdtCell(sb, item.DifferenceStatus, item.DifferenceStatus is "CONFERE" or "COMPATÍVEL" ? "SuccessCell" : "DangerCell");
                sb.Append("</table:table-row>");
            }
            sb.Append("</table:table>");
        }
        sb.Append("</office:text></office:body></office:document-content>");
        return sb.ToString();
    }

    private static IOrderedEnumerable<PlanCallRecord> OrderForProfessionalRelation(IEnumerable<PlanCallRecord> items)
        => items.OrderByDescending(x => x.RegionDistanceOrder)
            .ThenBy(x => x.Region, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);

    private static string ContactFor(PlanCallRecord item)
        => string.Join("\n", new[] { item.EffectivePhone, item.AlternatePhone }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.CurrentCultureIgnoreCase));

    private static void OdtCell(StringBuilder sb, string value, string style = "BodyCell")
    {
        sb.Append("<table:table-cell table:style-name=\"").Append(style).Append("\"><text:p text:style-name=\"BodyText\">");
        var lines = (value ?? string.Empty).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append("<text:line-break/>");
            sb.Append(Xml(lines[i]));
        }
        sb.Append("</text:p></table:table-cell>");
    }

    private static void OdtNameCell(StringBuilder sb, PlanCallRecord item)
    {
        sb.Append("<table:table-cell table:style-name=\"BodyCell\"><text:p text:style-name=\"BodyText\">");
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
        var numberMatch = Regex.Match(first, @"^(.*?)(?:\s+(?:N(?:r|[.º°]|[uú]mero)?\s*)?(\d+[A-Za-z]?|S/?N))$", RegexOptions.IgnoreCase);
        if (numberMatch.Success) { first = numberMatch.Groups[1].Value.Trim(); number = numberMatch.Groups[2].Value.Trim(); }
        var cursor = 1;
        if (string.IsNullOrWhiteSpace(number) && parts.Count > 1)
        {
            var isolatedNumber = Regex.Match(parts[1], @"^(?:N(?:r|[.º°]|[uú]mero)?\s*)?(\d+[A-Za-z]?|S/?N)$", RegexOptions.IgnoreCase);
            if (isolatedNumber.Success)
            {
                number = isolatedNumber.Groups[1].Value;
                cursor = 2;
            }
        }
        var cityState = parts.Count > cursor ? parts[^1] : string.Empty;
        var districtIndex = parts.Count - 2;
        var district = districtIndex >= cursor ? parts[districtIndex] : string.Empty;
        var complement = districtIndex > cursor ? string.Join(", ", parts.Skip(cursor).Take(districtIndex - cursor)) : string.Empty;
        return new AddressParts
        {
            Street = first,
            Number = number,
            Complement = complement,
            District = district,
            CityState = cityState
        };
    }

    public static AddressParts CanonicalizeAddress(string street, string number, string complement, string district, string cityState) => new()
    {
        Street = CanonicalizeStreet(street),
        Number = CanonicalizeNumber(number),
        Complement = CanonicalizeComplement(complement),
        District = CanonicalizeDistrict(district),
        CityState = NormalizeCityState(cityState)
    };

    public static string FormatAddress(string? street, string? number, string? complement, string? district, string? cityState, string? fallback)
    {
        var canonical = CanonicalizeAddress(street ?? string.Empty, number ?? string.Empty, complement ?? string.Empty,
            district ?? string.Empty, cityState ?? string.Empty);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(canonical.Street)) parts.Add(canonical.Street);
        if (!string.IsNullOrWhiteSpace(canonical.Number)) parts.Add($"Nr {canonical.Number}");
        if (!string.IsNullOrWhiteSpace(canonical.Complement)) parts.Add(canonical.Complement);
        if (!string.IsNullOrWhiteSpace(canonical.District)) parts.Add(canonical.District);
        if (!string.IsNullOrWhiteSpace(canonical.CityState)) parts.Add(canonical.CityState);
        return parts.Count == 0 ? Clean(fallback) : string.Join(", ", parts);
    }

    public static bool AddressEquivalent(string? a, string? b)
        => CompareAddresses(a, b) is "CONFERE" or "COMPATÍVEL";

    public static string CompareAddresses(string? a, string? b)
    {
        var na = NormalizeAddress(a);
        var nb = NormalizeAddress(b);
        if (string.IsNullOrWhiteSpace(na) || string.IsNullOrWhiteSpace(nb)) return "DIVERGENTE";
        if (na == nb) return "CONFERE";

        var fa = AddressFingerprintFor(a);
        var fb = AddressFingerprintFor(b);
        if (!string.IsNullOrWhiteSpace(fa.Number) && !string.IsNullOrWhiteSpace(fb.Number)
            && !fa.Number.Equals(fb.Number, StringComparison.OrdinalIgnoreCase))
            return "DIVERGENTE";

        var streetSimilarity = Similarity(fa.Street, fb.Street);
        var streetOverlap = TokenOverlap(fa.Street, fb.Street);
        var fullOverlap = TokenOverlap(na, nb);
        if ((streetSimilarity >= 0.84 || streetOverlap >= 0.67) && fullOverlap >= 0.58)
            return "COMPATÍVEL";
        if (Similarity(na, nb) >= 0.86 && (string.IsNullOrWhiteSpace(fa.Number) || string.IsNullOrWhiteSpace(fb.Number) || fa.Number == fb.Number))
            return "COMPATÍVEL";
        return "DIVERGENTE";
    }

    public static bool PhoneEquivalent(string? a, string? b)
    {
        var da = Digits(a); var db = Digits(b);
        if (da.Length == 0 || db.Length == 0) return false;
        return da == db || (da.Length >= 8 && db.Length >= 8 && da[^8..] == db[^8..]);
    }

    public static string FormatPhone(string? value)
    {
        var raw = ImportValue(value);
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var digits = Digits(raw);
        if (digits.Length is 12 or 13 && digits.StartsWith("55", StringComparison.Ordinal)) digits = digits[2..];
        var formatted = digits.Length switch
        {
            11 => $"({digits[..2]}) {digits.Substring(2, 5)}-{digits[7..]}",
            10 => $"({digits[..2]}) {digits.Substring(2, 4)}-{digits[6..]}",
            9 => $"{digits[..5]}-{digits[5..]}",
            8 => $"{digits[..4]}-{digits[4..]}",
            _ => Clean(raw)
        };
        if (formatted == Clean(raw)) return formatted;
        var note = Regex.Replace(raw, @"[\d\s()+.\-/]+", " ").Trim(' ', '-', '(', ')');
        return string.IsNullOrWhiteSpace(note) ? formatted : $"{formatted} ({TitleAddress(note)})";
    }

    public static string FormatZipCode(string? value)
    {
        var digits = Digits(value);
        return digits.Length == 8 ? $"{digits[..5]}-{digits[5..]}" : Clean(value);
    }

    public static string RegionFor(string? address, string? cityState)
    {
        var text = Normalize($"{address} {cityState}");
        if (text.Contains("belo horizonte") || Regex.IsMatch(text, @"\bbh\b")) return "Belo Horizonte";
        foreach (var knownCity in new[]
        {
            "juiz de fora", "conselheiro lafaiete", "sete lagoas", "divinopolis", "ouro preto", "barbacena", "mariana",
            "sao joaquim de bicas", "sao jose da lapa", "taquaracu de minas", "ribeirao das neves", "pedro leopoldo",
            "jaboticatubas", "lagoa santa", "santa luzia", "nova serrana", "mateus leme", "mario campos", "nova uniao",
            "nova lima", "rio acima", "rio manso", "vespasiano", "brumadinho", "esmeraldas", "matozinhos", "capim branco",
            "itatiaiucu", "itabirito", "contagem", "florestal", "sarzedo", "igarape", "raposos", "itaguara", "confins",
            "betim", "sabara", "ibirite", "caete", "baldim"
        })
            if (text.Contains(knownCity)) return Title(knownCity);
        var cityName = Clean(cityState).Split('/', StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(cityName) ? "Região não identificada" : Title(cityName);
    }

    /// <summary>
    /// Prioridade operacional aproximada por município, tomando Belo Horizonte como
    /// referência. O valor serve apenas para ordenação; não é exibido como quilometragem.
    /// </summary>
    public static int RegionDistanceOrder(string? region) => Normalize(region) switch
    {
        "regiao nao identificada" => -1,
        "juiz de fora" => 260,
        "divinopolis" => 120,
        "barbacena" => 170,
        "nova serrana" => 115,
        "ouro preto" => 100,
        "mariana" => 115,
        "conselheiro lafaiete" => 100,
        "sete lagoas" => 75,
        "baldim" => 95,
        "itaguara" => 95,
        "itatiaiucu" => 80,
        "taquaracu de minas" => 70,
        "rio manso" => 68,
        "nova uniao" => 65,
        "jaboticatubas" => 65,
        "florestal" => 65,
        "esmeraldas" => 60,
        "mateus leme" => 60,
        "itabirito" => 60,
        "matozinhos" => 55,
        "capim branco" => 55,
        "igarape" => 55,
        "brumadinho" => 55,
        "caete" => 55,
        "pedro leopoldo" => 45,
        "lagoa santa" => 42,
        "confins" => 40,
        "sao joaquim de bicas" => 40,
        "rio acima" => 40,
        "raposos" => 35,
        "mario campos" => 35,
        "betim" => 35,
        "ribeirao das neves" => 32,
        "sao jose da lapa" => 32,
        "sarzedo" => 30,
        "vespasiano" => 30,
        "nova lima" => 28,
        "ibirite" => 25,
        "santa luzia" => 24,
        "contagem" => 21,
        "sabara" => 20,
        "belo horizonte" => 0,
        _ => 1000
    };

    private static int FindHeaderRow(IReadOnlyList<List<string>> table)
    {
        for (var i = 0; i < Math.Min(20, table.Count); i++)
        {
            var normalized = table[i].Select(NormalizeHeader).ToList();
            if (normalized.Any(x => x is "nome" or "nome completo" or "militar")) return i;
        }
        return 0;
    }

    private static string NormalizeHeader(string? value) => Normalize(value).Trim();
    private static string NormalizeAddress(string? value)
    {
        var normalized = Normalize(value);
        normalized = Regex.Replace(normalized, @"\b(rua|r|avenida|av|numero|nr|n|bairro)\b", " ");
        normalized = Regex.Replace(normalized, @"\b(apartamento|apto|apt|ap)\b", " ap ");
        normalized = Regex.Replace(normalized, @"\b(bloco|blc|bl)\b", " bloco ");
        normalized = Regex.Replace(normalized, @"\b(de|da|do|das|dos|e)\b", " ");
        return Regex.Replace(normalized, @"\s+", " ").Replace(" s n ", " ").Trim();
    }
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

    private static (string Street, string Number) AddressFingerprintFor(string? value)
    {
        var raw = Clean(value);
        var first = raw.Split(',', StringSplitOptions.TrimEntries).FirstOrDefault() ?? raw;
        var explicitNumber = Regex.Match(raw, @"\b(?:numero|n[uú]mero|nr|n)[.:º°\s-]*(?<n>\d+[a-z]?)\b", RegexOptions.IgnoreCase);
        var commaNumber = Regex.Match(raw, @"(?:^|,)\s*(?<n>\d+[a-z]?)\s*(?:,|$)", RegexOptions.IgnoreCase);
        var trailingNumber = Regex.Match(first, @"(?<n>\d+[a-z]?)\s*$", RegexOptions.IgnoreCase);
        var number = explicitNumber.Success ? explicitNumber.Groups["n"].Value
            : commaNumber.Success ? commaNumber.Groups["n"].Value
            : trailingNumber.Success ? trailingNumber.Groups["n"].Value
            : string.Empty;
        var street = Regex.Replace(first, @"\b(?:numero|n[uú]mero|nr|n)[.:º°\s-]*\d+[a-z]?\b", " ", RegexOptions.IgnoreCase);
        street = Regex.Replace(street, @"\d+[a-z]?\s*$", " ", RegexOptions.IgnoreCase);
        return (NormalizeAddress(street), Normalize(number));
    }

    private static double TokenOverlap(string a, string b)
    {
        var left = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var right = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (left.Count == 0 || right.Count == 0) return 0;
        return (double)left.Intersect(right).Count() / Math.Min(left.Count, right.Count);
    }

    private static string ImportValue(string? value)
    {
        var clean = Clean(value);
        var normalized = Normalize(clean);
        return Regex.IsMatch(clean, @"^-+$") || normalized is "na" or "n a" or "nao informado" or "sem informacao"
            ? string.Empty
            : clean;
    }

    private static string Clean(string? value) => Regex.Replace(value?.Trim() ?? string.Empty, @"\s+", " ");
    private static string First(params string?[] values) => values.Select(Clean).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    public static string Digits(string? value) => Regex.Replace(value ?? string.Empty, @"\D+", string.Empty);
    private static string Title(string? value) => TitleAddress(value);
    private static string TitleAddress(string? value)
    {
        var title = PtBr.TextInfo.ToTitleCase(Clean(value).ToLower(PtBr));
        return Regex.Replace(title, @"(?<=\s)(Da|De|Do|Das|Dos|E)(?=\s)", match => match.Value.ToLower(PtBr));
    }

    private static string CanonicalizeStreet(string? value)
    {
        var street = ImportValue(value).Trim(' ', ',', ';', '-');
        if (string.IsNullOrWhiteSpace(street)) return string.Empty;
        street = Regex.Replace(street, @"^(?:R|R\.)\s+", "Rua ", RegexOptions.IgnoreCase);
        street = Regex.Replace(street, @"^(?:Av|Av\.)\s+", "Avenida ", RegexOptions.IgnoreCase);
        street = Regex.Replace(street, @"^(?:Pca|Pça|Praca)\s+", "Praça ", RegexOptions.IgnoreCase);
        street = Regex.Replace(street, @"^(?:Rod|Rod\.)\s+", "Rodovia ", RegexOptions.IgnoreCase);
        if (!Regex.IsMatch(street, @"^(Rua|Avenida|Alameda|Travessa|Praça|Rodovia|Estrada|Largo|Via|BR\b|MG\b)", RegexOptions.IgnoreCase))
            street = "Rua " + street;
        return TitleAddress(street);
    }

    private static string CanonicalizeNumber(string? value)
    {
        var number = ImportValue(value);
        number = Regex.Replace(number, @"^(?:N(?:r|[.º°]|[uú]mero)?\s*)", string.Empty, RegexOptions.IgnoreCase).Trim();
        return number.Equals("sn", StringComparison.OrdinalIgnoreCase) || number.Equals("s/n", StringComparison.OrdinalIgnoreCase)
            ? "S/N"
            : number.ToUpperInvariant();
    }

    private static string CanonicalizeComplement(string? value)
    {
        var complement = ImportValue(value);
        if (string.IsNullOrWhiteSpace(complement)) return string.Empty;
        var invertedApartment = Regex.Match(complement, @"^Bloco\s+(?<bloco>[A-Za-z0-9]+)\s+(?<apto>\d+[A-Za-z]?)\s+Ap$", RegexOptions.IgnoreCase);
        if (invertedApartment.Success)
            return $"Bloco {invertedApartment.Groups["bloco"].Value.ToUpperInvariant()}, Apto {invertedApartment.Groups["apto"].Value.ToUpperInvariant()}";
        if (Regex.IsMatch(complement, @"^\d+[A-Za-z]?$")) complement = "Apto " + complement.ToUpperInvariant();
        complement = Regex.Replace(complement, @"\b(?:Apartamento|Apto|Apt|Ap)[.]?\b", "Apto", RegexOptions.IgnoreCase);
        complement = Regex.Replace(complement, @"\b(?:Bloco|Blc|Bl)[.]?\b", "Bloco", RegexOptions.IgnoreCase);
        return TitleAddress(complement);
    }

    private static string CanonicalizeDistrict(string? value)
    {
        var district = ImportValue(value);
        district = Regex.Replace(district, @"^Bairro\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        return TitleAddress(district);
    }

    private static string NormalizeCityState(string? value)
    {
        var text = ImportValue(value);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = Regex.Replace(text, @"\s*[-–—]\s*", "/");
        if (!text.Contains('/'))
        {
            var trailingState = Regex.Match(text, @"^(?<city>.+?)\s+(?<state>[A-Za-z]{2,3})$");
            if (trailingState.Success) text = $"{trailingState.Groups["city"].Value}/{trailingState.Groups["state"].Value}";
        }
        var parts = text.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) return $"{TitleAddress(parts[0])}/{parts[^1].ToUpperInvariant()}";
        var city = TitleAddress(text);
        var knownMg = new HashSet<string>(StringComparer.Ordinal)
        {
            "belo horizonte", "contagem", "betim", "santa luzia", "ribeirao das neves", "sabara", "ibirite",
            "nova lima", "juiz de fora", "conselheiro lafaiete", "sete lagoas", "vespasiano", "lagoa santa",
            "nova serrana", "ouro preto", "mariana", "barbacena", "divinopolis"
        };
        return knownMg.Contains(Normalize(city)) ? $"{city}/MG" : city;
    }
    private static string Xml(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;

    private const string OdtManifest = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.2\"><manifest:file-entry manifest:full-path=\"/\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/><manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/><manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/></manifest:manifest>";
    private const string OdtStyles = """
        <?xml version="1.0" encoding="UTF-8"?>
        <office:document-styles xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" office:version="1.2">
          <office:styles>
            <style:default-style style:family="paragraph"><style:text-properties style:font-name="Arial" fo:font-size="8.5pt"/></style:default-style>
            <style:style style:name="Bold" style:family="text"><style:text-properties fo:font-weight="bold"/></style:style>
            <style:style style:name="Title" style:family="paragraph"><style:paragraph-properties fo:text-align="center" fo:margin-bottom="0.08cm"/><style:text-properties fo:font-size="17pt" fo:font-weight="bold" fo:color="#17365D"/></style:style>
            <style:style style:name="Subtitle" style:family="paragraph"><style:paragraph-properties fo:text-align="center" fo:margin-bottom="0.08cm"/><style:text-properties fo:font-size="9.5pt" fo:font-weight="bold" fo:color="#365F91"/></style:style>
            <style:style style:name="Meta" style:family="paragraph"><style:paragraph-properties fo:text-align="center" fo:margin-bottom="0.35cm"/><style:text-properties fo:font-size="8pt" fo:color="#666666"/></style:style>
            <style:style style:name="RegionHeading" style:family="paragraph"><style:paragraph-properties fo:background-color="#17365D" fo:padding="0.12cm" fo:margin-top="0.24cm" fo:margin-bottom="0.08cm"/><style:text-properties fo:font-size="10pt" fo:font-weight="bold" fo:color="#FFFFFF"/></style:style>
            <style:style style:name="HeaderText" style:family="paragraph"><style:paragraph-properties fo:text-align="center"/><style:text-properties fo:font-size="7.5pt" fo:font-weight="bold" fo:color="#FFFFFF"/></style:style>
            <style:style style:name="BodyText" style:family="paragraph"><style:text-properties fo:font-size="7.5pt"/></style:style>
            <style:style style:name="Footer" style:family="paragraph"><style:paragraph-properties fo:text-align="center"/><style:text-properties fo:font-size="7pt" fo:color="#777777"/></style:style>
            <style:style style:name="PlanTable" style:family="table"><style:table-properties style:width="27cm" table:align="center"/></style:style>
            <style:style style:name="HeaderCell" style:family="table-cell"><style:table-cell-properties fo:background-color="#365F91" fo:border="0.02cm solid #FFFFFF" fo:padding="0.07cm"/></style:style>
            <style:style style:name="BodyCell" style:family="table-cell"><style:table-cell-properties fo:border="0.02cm solid #B8C6D9" fo:padding="0.07cm" style:vertical-align="middle"/></style:style>
            <style:style style:name="SuccessCell" style:family="table-cell"><style:table-cell-properties fo:background-color="#E2F0D9" fo:border="0.02cm solid #A9C68E" fo:padding="0.07cm" style:vertical-align="middle"/></style:style>
            <style:style style:name="DangerCell" style:family="table-cell"><style:table-cell-properties fo:background-color="#FCE4D6" fo:border="0.02cm solid #D9A38F" fo:padding="0.07cm" style:vertical-align="middle"/></style:style>
            <style:style style:name="ColNumber" style:family="table-column"><style:table-column-properties style:column-width="0.7cm"/></style:style>
            <style:style style:name="ColRank" style:family="table-column"><style:table-column-properties style:column-width="1.35cm"/></style:style>
            <style:style style:name="ColName" style:family="table-column"><style:table-column-properties style:column-width="4.15cm"/></style:style>
            <style:style style:name="ColPhone" style:family="table-column"><style:table-column-properties style:column-width="2.65cm"/></style:style>
            <style:style style:name="ColPlanAddress" style:family="table-column"><style:table-column-properties style:column-width="6.15cm"/></style:style>
            <style:style style:name="ColReferenceAddress" style:family="table-column"><style:table-column-properties style:column-width="7.15cm"/></style:style>
            <style:style style:name="ColStatus" style:family="table-column"><style:table-column-properties style:column-width="2.85cm"/></style:style>
          </office:styles>
          <office:automatic-styles><style:page-layout style:name="Landscape"><style:page-layout-properties fo:page-width="29.7cm" fo:page-height="21cm" style:print-orientation="landscape" fo:margin="1.1cm"/></style:page-layout></office:automatic-styles>
          <office:master-styles><style:master-page style:name="Standard" style:page-layout-name="Landscape"><style:footer><text:p text:style-name="Footer">SIGFUR • Plano de Chamada • Página <text:page-number/></text:p></style:footer></style:master-page></office:master-styles>
        </office:document-styles>
        """;
}
