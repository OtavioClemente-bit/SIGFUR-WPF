using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class LicensedTransferredRepository
{
    private readonly AppPaths _paths;
    private readonly MilitaryRepository _militaryRepository;
    private readonly LogService _log;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public LicensedTransferredRepository(AppPaths paths, MilitaryRepository militaryRepository, LogService log)
    {
        _paths = paths;
        _militaryRepository = militaryRepository;
        _log = log;
    }

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
            await _militaryRepository.EnsureSchemaAsync(cancellationToken);
            await using var connection = OpenConnection();
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS lt_militares(
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        posto TEXT,nome TEXT NOT NULL,nome_guerra TEXT,cpf TEXT,prec_cp TEXT,idt TEXT,
                        banco TEXT,agencia TEXT,conta TEXT,foto TEXT,ano TEXT,data_nascimento TEXT,data_praca TEXT,
                        endereco TEXT,cep TEXT,recebe_pre_escolar TEXT,valor_pre_escolar TEXT,
                        recebe_aux_transporte TEXT,valor_aux_transporte TEXT,pnr TEXT,motivo TEXT,destino TEXT,
                        visivel INTEGER DEFAULT 1,telefone TEXT,email TEXT,escolaridade TEXT,
                        pensao_alimenticia TEXT DEFAULT 'Não',valor_pensao TEXT,
                        aux_total_bruto REAL,aux_dias_uteis INTEGER,aux_base_ts TEXT,
                        adido_encostado INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_lt_nome ON lt_militares(nome COLLATE NOCASE);
                    CREATE INDEX IF NOT EXISTS idx_lt_cpf ON lt_militares(cpf);
                    """;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await EnsureColumnsAsync(connection, "lt_militares", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["telefone"] = "TEXT", ["email"] = "TEXT", ["escolaridade"] = "TEXT",
                ["pensao_alimenticia"] = "TEXT DEFAULT 'Não'", ["valor_pensao"] = "TEXT",
                ["aux_total_bruto"] = "REAL", ["aux_dias_uteis"] = "INTEGER", ["aux_base_ts"] = "TEXT",
                ["adido_encostado"] = "INTEGER NOT NULL DEFAULT 0", ["visivel"] = "INTEGER DEFAULT 1"
            }, cancellationToken);
            await NormalizeStoredNamesAsync(connection, cancellationToken);
            _schemaReady = true;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha preparando Licenciados/Transferidos nativo.", ex);
            throw;
        }
        finally { _schemaGate.Release(); }
    }

    public async Task<List<LicensedTransferredRecord>> GetAllAsync(bool includeHidden = false, string? filter = null, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<LicensedTransferredRecord>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT lt.*,
                   COALESCE(NULLIF(lt.telefone,''),NULLIF(r.telefone,''),'') AS contato_telefone,
                   COALESCE(NULLIF(lt.email,''),NULLIF(r.email,''),'') AS contato_email,
                   COALESCE(NULLIF(lt.escolaridade,''),NULLIF(r.escolaridade,''),'') AS contato_escolaridade
              FROM lt_militares lt
              LEFT JOIN recrutas_extra r
                ON REPLACE(REPLACE(REPLACE(r.cpf,'.',''),'-',''),' ','') = REPLACE(REPLACE(REPLACE(lt.cpf,'.',''),'-',''),' ','')
             WHERE ($hidden=1 OR COALESCE(lt.visivel,1)=1)
             ORDER BY lt.nome COLLATE NOCASE, lt.id;
            """;
        command.Parameters.AddWithValue("$hidden", includeHidden ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadRecord(reader));

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var query = Normalize(filter);
            var digits = MilitaryFormatting.Digits(filter);
            result = result.Where(x =>
            {
                var text = Normalize($"{x.Rank} {x.Name} {x.WarName} {x.Cpf} {x.PrecCp} {x.MilitaryId} {x.Reason} {x.Destination} {x.FormationYear} {x.Phone} {x.Email} {x.Address} {x.ZipCode}");
                var numbers = MilitaryFormatting.Digits($"{x.Cpf} {x.PrecCp} {x.MilitaryId} {x.Phone} {x.ZipCode}");
                return text.Contains(query) || (!string.IsNullOrWhiteSpace(digits) && numbers.Contains(digits));
            }).ToList();
        }
        return result;
    }

    public async Task<LicensedTransferredRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT lt.*,
                   COALESCE(NULLIF(lt.telefone,''),NULLIF(r.telefone,''),'') AS contato_telefone,
                   COALESCE(NULLIF(lt.email,''),NULLIF(r.email,''),'') AS contato_email,
                   COALESCE(NULLIF(lt.escolaridade,''),NULLIF(r.escolaridade,''),'') AS contato_escolaridade
              FROM lt_militares lt
              LEFT JOIN recrutas_extra r
                ON REPLACE(REPLACE(REPLACE(r.cpf,'.',''),'-',''),' ','') = REPLACE(REPLACE(REPLACE(lt.cpf,'.',''),'-',''),' ','')
             WHERE lt.id=$id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public async Task<int> SaveAsync(LicensedTransferredRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Name)) throw new InvalidOperationException("Informe o nome do militar.");
        await EnsureSchemaAsync(cancellationToken);
        record.Rank = MilitaryRankService.Canonicalize(record.Rank);
        record.Name = UpperName(record.Name);
        record.WarName = UpperName(record.WarName);
        record.Cpf = MilitaryFormatting.Digits(record.Cpf);
        record.BirthDate = MilitaryFormatting.NormalizeDateText(record.BirthDate);
        record.EnlistmentDate = MilitaryFormatting.NormalizeDateText(record.EnlistmentDate);

        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            AddParameters(command, record);
            if (record.Id == 0)
            {
                // IDs negativos formam um namespace próprio para LT e impedem colisão
                // com militares ativos nas tabelas compartilhadas de documentos e AT.
                record.Id = await NextNegativeIdAsync(connection, (SqliteTransaction)transaction, cancellationToken);
                command.CommandText = """
                    INSERT INTO lt_militares(
                        id,posto,nome,nome_guerra,cpf,prec_cp,idt,banco,agencia,conta,foto,ano,data_nascimento,data_praca,
                        endereco,cep,recebe_pre_escolar,valor_pre_escolar,recebe_aux_transporte,valor_aux_transporte,pnr,
                        motivo,destino,visivel,telefone,email,escolaridade,pensao_alimenticia,valor_pensao,
                        aux_total_bruto,aux_dias_uteis,aux_base_ts,adido_encostado)
                    VALUES($id,$posto,$nome,$ng,$cpf,$prec,$idt,$banco,$agencia,$conta,$foto,$ano,$nascimento,$praca,
                        $endereco,$cep,$rpe,$vpe,$rat,$vat,$pnr,$motivo,$destino,$visivel,$telefone,$email,$escolaridade,
                        $pensao,$valorPensao,$auxBruto,$auxDias,$auxTs,$adido);
                    """;
                command.Parameters.AddWithValue("$id", record.Id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                command.CommandText = """
                    UPDATE lt_militares SET
                        posto=$posto,nome=$nome,nome_guerra=$ng,cpf=$cpf,prec_cp=$prec,idt=$idt,banco=$banco,agencia=$agencia,
                        conta=$conta,foto=$foto,ano=$ano,data_nascimento=$nascimento,data_praca=$praca,endereco=$endereco,cep=$cep,
                        recebe_pre_escolar=$rpe,valor_pre_escolar=$vpe,recebe_aux_transporte=$rat,valor_aux_transporte=$vat,pnr=$pnr,
                        motivo=$motivo,destino=$destino,visivel=$visivel,telefone=$telefone,email=$email,escolaridade=$escolaridade,
                        pensao_alimenticia=$pensao,valor_pensao=$valorPensao,aux_total_bruto=$auxBruto,aux_dias_uteis=$auxDias,
                        aux_base_ts=$auxTs,adido_encostado=$adido
                    WHERE id=$id;
                    """;
                command.Parameters.AddWithValue("$id", record.Id);
                if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                    throw new InvalidOperationException("Registro de Licenciados/Transferidos não encontrado.");
            }

            if (!string.IsNullOrWhiteSpace(record.Cpf))
            {
                await using var extras = connection.CreateCommand();
                extras.Transaction = (SqliteTransaction)transaction;
                extras.CommandText = """
                    INSERT INTO recrutas_extra(cpf,telefone,email,escolaridade,criado_em)
                    VALUES($cpf,$telefone,$email,$escolaridade,$agora)
                    ON CONFLICT(cpf) DO UPDATE SET telefone=excluded.telefone,email=excluded.email,escolaridade=excluded.escolaridade;
                    """;
                extras.Parameters.AddWithValue("$cpf", record.Cpf);
                extras.Parameters.AddWithValue("$telefone", record.Phone ?? string.Empty);
                extras.Parameters.AddWithValue("$email", record.Email ?? string.Empty);
                extras.Parameters.AddWithValue("$escolaridade", record.Education ?? string.Empty);
                extras.Parameters.AddWithValue("$agora", DateTime.Now.ToString("s"));
                await extras.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return record.Id;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        var list = ids.Distinct().Where(x => x != 0).ToList();
        if (list.Count == 0) return;
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in list)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM lt_militares WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetVisibleAsync(IEnumerable<int> ids, bool visible, CancellationToken cancellationToken = default)
    {
        var list = ids.Distinct().Where(x => x != 0).ToList();
        if (list.Count == 0) return;
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in list)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE lt_militares SET visivel=$visible WHERE id=$id;";
            command.Parameters.AddWithValue("$visible", visible ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> RestoreToActiveAsync(LicensedTransferredRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Id == 0) throw new InvalidOperationException("Registro LT inválido.");
        if (string.IsNullOrWhiteSpace(record.Name)) throw new InvalidOperationException("Informe o nome.");
        if (string.IsNullOrWhiteSpace(record.WarName)) throw new InvalidOperationException("Informe o Nome de Guerra antes de restaurar.");
        if (string.IsNullOrWhiteSpace(record.MilitaryId)) throw new InvalidOperationException("Informe a IDT antes de restaurar.");
        await EnsureSchemaAsync(cancellationToken);

        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ValidateNoActiveDuplicateAsync(connection, (SqliteTransaction)transaction, record, cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO militares(
                    posto,nome,nome_guerra,cpf,prec_cp,idt,banco,agencia,conta,foto,ano,data_nascimento,data_praca,endereco,cep,
                    recebe_pre_escolar,valor_pre_escolar,recebe_aux_transporte,valor_aux_transporte,pnr,pensao_alimenticia,valor_pensao,
                    aux_total_bruto,aux_dias_uteis,aux_base_ts,adido_encostado)
                VALUES($posto,$nome,$ng,$cpf,$prec,$idt,$banco,$agencia,$conta,$foto,$ano,$nascimento,$praca,$endereco,$cep,
                    $rpe,$vpe,$rat,$vat,$pnr,$pensao,$valorPensao,$auxBruto,$auxDias,$auxTs,$adido);
                SELECT last_insert_rowid();
                """;
            AddParameters(insert, record);
            var newId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));

            await using (var contact = connection.CreateCommand())
            {
                contact.Transaction = (SqliteTransaction)transaction;
                contact.CommandText = """
                    INSERT INTO militares_contato(militar_id,cpf,telefone,email,atualizado_em)
                    VALUES($id,$cpf,$telefone,$email,$agora)
                    ON CONFLICT(militar_id) DO UPDATE SET cpf=excluded.cpf,telefone=excluded.telefone,email=excluded.email,atualizado_em=excluded.atualizado_em;
                    """;
                contact.Parameters.AddWithValue("$id", newId);
                contact.Parameters.AddWithValue("$cpf", MilitaryFormatting.Digits(record.Cpf));
                contact.Parameters.AddWithValue("$telefone", record.Phone ?? string.Empty);
                contact.Parameters.AddWithValue("$email", record.Email ?? string.Empty);
                contact.Parameters.AddWithValue("$agora", DateTime.Now.ToString("s"));
                await contact.ExecuteNonQueryAsync(cancellationToken);
            }

            if (newId != record.Id)
            {
                await MoveLinkedDataAsync(connection, (SqliteTransaction)transaction, record.Id, newId, cancellationToken);
                await using var oldContact = connection.CreateCommand();
                oldContact.Transaction = (SqliteTransaction)transaction;
                oldContact.CommandText = "DELETE FROM militares_contato WHERE militar_id=$id;";
                oldContact.Parameters.AddWithValue("$id", record.Id);
                await oldContact.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM lt_militares WHERE id=$id;";
                delete.Parameters.AddWithValue("$id", record.Id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return newId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<MilitaryDocumentRecord>> GetDocumentsAsync(int id, CancellationToken cancellationToken = default)
        => await _militaryRepository.GetDocumentsAsync(id, cancellationToken);

    public async Task<MilitaryDocumentRecord> AddDocumentAsync(
        LicensedTransferredRecord record, string sourcePath, string type, string title,
        string observation = "", string keysJson = "", CancellationToken cancellationToken = default)
        => await _militaryRepository.AddDocumentAsync(record.ToMilitaryRecord(), sourcePath, type, title, observation, keysJson, cancellationToken);

    public async Task RemoveDocumentAsync(MilitaryDocumentRecord document, bool deletePhysicalFile, CancellationToken cancellationToken = default)
        => await _militaryRepository.RemoveDocumentAsync(document, deletePhysicalFile, cancellationToken);

    public async Task<IReadOnlyList<LicensedTransportFare>> GetTransportFaresAsync(int id, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var list = new List<LicensedTransportFare>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT idx,tarifa FROM aux_transporte_tarifas WHERE militar_id=$id ORDER BY idx;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            list.Add(new LicensedTransportFare { Index = reader.GetInt32(0), Fare = reader.IsDBNull(1) ? 0 : reader.GetDouble(1) });
        return list;
    }

    public async Task<decimal> GetSalaryAsync(string rank, CancellationToken cancellationToken = default)
        => await _militaryRepository.GetSalaryByRankAsync(rank, cancellationToken);

    public async Task<string> ExportWalletAsync(
        LicensedTransferredRecord record,
        string destinationZip,
        IReadOnlyList<PaystubFileRecord> paystubs,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var documents = await GetDocumentsAsync(record.Id, cancellationToken);
        var fares = await GetTransportFaresAsync(record.Id, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationZip) ?? _paths.DataDirectory);
        if (File.Exists(destinationZip)) File.Delete(destinationZip);

        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(destinationZip, ZipArchiveMode.Create);
            var lines = new List<string>
            {
                "CARTEIRA — LICENCIADO / TRANSFERIDO",
                new string('=', 64),
                $"Posto/Graduação: {record.Rank}", $"Nome: {record.Name}", $"Nome de Guerra: {record.WarName}",
                $"CPF: {record.FormattedCpf}", $"PREC-CP: {record.FormattedPrecCp}", $"IDT: {record.MilitaryId}",
                $"Telefone: {record.Phone}", $"E-mail: {record.Email}", $"Escolaridade: {record.Education}",
                $"Banco: {record.Bank}", $"Agência: {record.Agency}", $"Conta: {record.Account}",
                $"Nascimento: {record.BirthDate}", $"Data de praça: {record.EnlistmentDate}",
                $"Endereço: {record.Address}", $"CEP: {record.ZipCode}",
                $"Pré-escolar: {record.ReceivesPreSchool} — {record.PreSchoolValue}",
                $"Auxílio-Transporte: {record.ReceivesTransportAid} — {record.TransportAidValue}",
                $"PNR: {record.HasPnr}", $"Pensão: {record.Alimony} — {record.AlimonyValue}",
                $"Motivo: {record.Reason}", $"Destino/OM: {record.Destination}", $"Status: {record.StatusText}",
                "", "TARIFAS DE AUXÍLIO-TRANSPORTE", new string('-', 64)
            };
            lines.AddRange(fares.Count == 0 ? ["Nenhuma tarifa cadastrada."] : fares.Select((x, i) => $"{i + 1}. {x.Display}"));
            lines.AddRange(["", "DOCUMENTOS", new string('-', 64)]);
            lines.AddRange(documents.Count == 0 ? ["Nenhum documento salvo."] : documents.Select((x, i) => $"{i + 1}. {x.Title} — {x.FileName}"));
            lines.AddRange(["", "CONTRACHEQUES", new string('-', 64)]);
            lines.AddRange(paystubs.Count == 0 ? ["Nenhum contracheque localizado."] : paystubs.Select((x, i) => $"{i + 1}. {x.FileName}"));

            var dataEntry = archive.CreateEntry("00_CARTEIRA_DADOS.txt", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(dataEntry.Open(), new UTF8Encoding(true))) writer.Write(string.Join(Environment.NewLine, lines));
            var manifestEntry = archive.CreateEntry("manifesto.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(new
                {
                    gerado_em = DateTime.Now,
                    militar = record,
                    documentos = documents.Select(x => new { x.Title, x.FileName, x.Type, x.SavedAt }),
                    contracheques = paystubs.Select(x => x.FileName),
                    aux_tarifas = fares
                }, new JsonSerializerOptions { WriteIndented = true }));

            AddFile(archive, record.PhotoPath, "foto");
            foreach (var document in documents) AddFile(archive, document.Path, "documentos_certidoes");
            foreach (var paystub in paystubs) AddFile(archive, paystub.Path, "contracheques");
        }, cancellationToken);
        return destinationZip;
    }

    private static void AddFile(ZipArchive archive, string? source, string folder)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;
        var fileName = Path.GetFileName(source);
        var entryName = $"{folder}/{fileName}";
        var suffix = 2;
        while (archive.GetEntry(entryName) is not null)
        {
            entryName = $"{folder}/{Path.GetFileNameWithoutExtension(fileName)}_{suffix++}{Path.GetExtension(fileName)}";
        }
        archive.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
    }

    private static async Task MoveLinkedDataAsync(SqliteConnection connection, SqliteTransaction transaction, int sourceId, int destinationId, CancellationToken cancellationToken)
    {
        await using (var fares = connection.CreateCommand())
        {
            fares.Transaction = transaction;
            fares.CommandText = "INSERT OR REPLACE INTO aux_transporte_tarifas(militar_id,idx,tarifa) SELECT $dst,idx,tarifa FROM aux_transporte_tarifas WHERE militar_id=$src; DELETE FROM aux_transporte_tarifas WHERE militar_id=$src;";
            fares.Parameters.AddWithValue("$src", sourceId); fares.Parameters.AddWithValue("$dst", destinationId);
            await fares.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var intervals = connection.CreateCommand())
        {
            intervals.Transaction = transaction;
            intervals.CommandText = """
                INSERT INTO tempo_servico_intervalos(militar_id,data_inicio,data_fim,observacao,ordem,ativo,criado_em,atualizado_em)
                SELECT $dst,data_inicio,data_fim,observacao,ordem,ativo,criado_em,atualizado_em FROM tempo_servico_intervalos WHERE militar_id=$src;
                DELETE FROM tempo_servico_intervalos WHERE militar_id=$src;
                """;
            intervals.Parameters.AddWithValue("$src", sourceId); intervals.Parameters.AddWithValue("$dst", destinationId);
            await intervals.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var documents = connection.CreateCommand())
        {
            documents.Transaction = transaction;
            documents.CommandText = "UPDATE militar_documentos SET militar_id=$dst WHERE militar_id=$src;";
            documents.Parameters.AddWithValue("$src", sourceId); documents.Parameters.AddWithValue("$dst", destinationId);
            await documents.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ValidateNoActiveDuplicateAsync(SqliteConnection connection, SqliteTransaction transaction, LicensedTransferredRecord record, CancellationToken cancellationToken)
    {
        var cpf = MilitaryFormatting.Digits(record.Cpf);
        var prec = NormalizeIdentifier(record.PrecCp);
        var idt = NormalizeIdentifier(record.MilitaryId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT cpf,prec_cp,idt FROM militares;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(cpf) && MilitaryFormatting.Digits(reader.IsDBNull(0) ? "" : reader.GetString(0)) == cpf)
                throw new InvalidOperationException("Já existe um militar com o mesmo CPF na lista principal.");
            if (!string.IsNullOrWhiteSpace(prec) && NormalizeIdentifier(reader.IsDBNull(1) ? "" : reader.GetString(1)) == prec)
                throw new InvalidOperationException("Já existe um militar com o mesmo PREC-CP na lista principal.");
            if (!string.IsNullOrWhiteSpace(idt) && NormalizeIdentifier(reader.IsDBNull(2) ? "" : reader.GetString(2)) == idt)
                throw new InvalidOperationException("Já existe um militar com a mesma IDT na lista principal.");
        }
    }

    private static void AddParameters(SqliteCommand command, LicensedTransferredRecord record)
    {
        command.Parameters.AddWithValue("$posto", MilitaryRankService.Canonicalize(record.Rank));
        command.Parameters.AddWithValue("$nome", UpperName(record.Name));
        command.Parameters.AddWithValue("$ng", UpperName(record.WarName));
        command.Parameters.AddWithValue("$cpf", MilitaryFormatting.Digits(record.Cpf));
        command.Parameters.AddWithValue("$prec", record.PrecCp.Trim());
        command.Parameters.AddWithValue("$idt", record.MilitaryId.Trim());
        command.Parameters.AddWithValue("$banco", record.Bank.Trim());
        command.Parameters.AddWithValue("$agencia", record.Agency.Trim());
        command.Parameters.AddWithValue("$conta", record.Account.Trim());
        command.Parameters.AddWithValue("$foto", record.PhotoPath.Trim());
        command.Parameters.AddWithValue("$ano", record.FormationYear.Trim());
        command.Parameters.AddWithValue("$nascimento", MilitaryFormatting.NormalizeDateText(record.BirthDate));
        command.Parameters.AddWithValue("$praca", MilitaryFormatting.NormalizeDateText(record.EnlistmentDate));
        command.Parameters.AddWithValue("$endereco", record.Address.Trim());
        command.Parameters.AddWithValue("$cep", MilitaryFormatting.Digits(record.ZipCode));
        command.Parameters.AddWithValue("$rpe", NormalizeYesNo(record.ReceivesPreSchool));
        command.Parameters.AddWithValue("$vpe", record.PreSchoolValue.Trim());
        command.Parameters.AddWithValue("$rat", NormalizeYesNo(record.ReceivesTransportAid));
        command.Parameters.AddWithValue("$vat", record.TransportAidValue.Trim());
        command.Parameters.AddWithValue("$pnr", NormalizeYesNo(record.HasPnr));
        command.Parameters.AddWithValue("$motivo", record.Reason.Trim());
        command.Parameters.AddWithValue("$destino", record.Destination.Trim());
        command.Parameters.AddWithValue("$visivel", record.IsVisible ? 1 : 0);
        command.Parameters.AddWithValue("$telefone", record.Phone.Trim());
        command.Parameters.AddWithValue("$email", record.Email.Trim());
        command.Parameters.AddWithValue("$escolaridade", record.Education.Trim());
        command.Parameters.AddWithValue("$pensao", NormalizeYesNo(record.Alimony));
        command.Parameters.AddWithValue("$valorPensao", record.AlimonyValue.Trim());
        command.Parameters.AddWithValue("$auxBruto", record.TransportGrossTotal is null ? DBNull.Value : record.TransportGrossTotal.Value);
        command.Parameters.AddWithValue("$auxDias", record.TransportWorkingDays is null ? DBNull.Value : record.TransportWorkingDays.Value);
        command.Parameters.AddWithValue("$auxTs", record.TransportBaseTimestamp ?? string.Empty);
        command.Parameters.AddWithValue("$adido", 0);
    }

    private static LicensedTransferredRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Id = GetInt(reader, "id"), Rank = GetString(reader, "posto"), Name = UpperName(GetString(reader, "nome")), WarName = UpperName(GetString(reader, "nome_guerra")),
        Cpf = GetString(reader, "cpf"), PrecCp = GetString(reader, "prec_cp"), MilitaryId = GetString(reader, "idt"),
        Bank = GetString(reader, "banco"), Agency = GetString(reader, "agencia"), Account = GetString(reader, "conta"), PhotoPath = GetString(reader, "foto"),
        FormationYear = GetString(reader, "ano"), BirthDate = GetString(reader, "data_nascimento"), EnlistmentDate = GetString(reader, "data_praca"),
        Address = GetString(reader, "endereco"), ZipCode = GetString(reader, "cep"), ReceivesPreSchool = GetString(reader, "recebe_pre_escolar", "Não"),
        PreSchoolValue = GetString(reader, "valor_pre_escolar", "0.00"), ReceivesTransportAid = GetString(reader, "recebe_aux_transporte", "Não"),
        TransportAidValue = GetString(reader, "valor_aux_transporte", "0.00"), HasPnr = GetString(reader, "pnr", "Não"),
        Reason = GetString(reader, "motivo"), Destination = GetString(reader, "destino"), IsVisible = GetInt(reader, "visivel", 1) != 0,
        Phone = GetString(reader, "contato_telefone"), Email = GetString(reader, "contato_email"), Education = GetString(reader, "contato_escolaridade"),
        Alimony = GetString(reader, "pensao_alimenticia", "Não"), AlimonyValue = GetString(reader, "valor_pensao"),
        TransportGrossTotal = GetNullableDouble(reader, "aux_total_bruto"), TransportWorkingDays = GetNullableInt(reader, "aux_dias_uteis"),
        TransportBaseTimestamp = GetString(reader, "aux_base_ts")
    };

    private static string UpperName(string? value)
        => string.Join(" ", (value ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));

    private static async Task NormalizeStoredNamesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<(int Id, string Name, string WarName)>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id,COALESCE(nome,''),COALESCE(nome_guerra,'') FROM lt_militares;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        foreach (var row in rows)
        {
            var name = UpperName(row.Name);
            var war = UpperName(row.WarName);
            if (name == row.Name && war == row.WarName) continue;
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE lt_militares SET nome=$nome,nome_guerra=$ng WHERE id=$id;";
            update.Parameters.AddWithValue("$nome", name);
            update.Parameters.AddWithValue("$ng", war);
            update.Parameters.AddWithValue("$id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> NextNegativeIdAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MIN(id),0) FROM lt_militares WHERE id<0;";
        var current = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture);
        return current >= 0 ? -1 : checked(current - 1);
    }

    private static async Task EnsureColumnsAsync(SqliteConnection connection, string table, IReadOnlyDictionary<string, string> columns, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
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

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    }
    private static string NormalizeIdentifier(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormalizeYesNo(string? value) => MilitaryRecord.IsYes(value) ? "Sim" : "Não";
    private static int GetInt(SqliteDataReader reader, string name, int fallback = 0) { try { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? fallback : Convert.ToInt32(reader.GetValue(i)); } catch { return fallback; } }
    private static int? GetNullableInt(SqliteDataReader reader, string name) { try { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i)); } catch { return null; } }
    private static double? GetNullableDouble(SqliteDataReader reader, string name) { try { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? null : Convert.ToDouble(reader.GetValue(i), CultureInfo.InvariantCulture); } catch { return null; } }
    private static string GetString(SqliteDataReader reader, string name, string fallback = "") { try { var i = reader.GetOrdinal(name); return reader.IsDBNull(i) ? fallback : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? fallback; } catch { return fallback; } }
}
