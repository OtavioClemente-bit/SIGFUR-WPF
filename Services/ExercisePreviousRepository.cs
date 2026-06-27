using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class ExercisePreviousRepository
{
    private readonly AppPaths _paths;
    private readonly LogService _log;

    private static readonly (string Property, string Column)[] ProcessMap =
    [
        (nameof(ExercisePreviousProcess.MilitaryId), "militar_id"),
        (nameof(ExercisePreviousProcess.Rank), "posto_grad"), (nameof(ExercisePreviousProcess.FullName), "nome_completo"),
        (nameof(ExercisePreviousProcess.WarName), "nome_guerra"), (nameof(ExercisePreviousProcess.PrecCp), "prec_cp"),
        (nameof(ExercisePreviousProcess.Identity), "idt"), (nameof(ExercisePreviousProcess.Cpf), "cpf"),
        (nameof(ExercisePreviousProcess.GeneralProtocol), "protocolo_geral"), (nameof(ExercisePreviousProcess.Section), "secao"),
        (nameof(ExercisePreviousProcess.SubjectNumber), "assunto_num"), (nameof(ExercisePreviousProcess.SubjectText), "assunto_texto"),
        (nameof(ExercisePreviousProcess.AttachmentSheets), "anexos_folhas"), (nameof(ExercisePreviousProcess.Recipient), "destinatario"),
        (nameof(ExercisePreviousProcess.Object), "objeto"), (nameof(ExercisePreviousProcess.Phone), "telefone"),
        (nameof(ExercisePreviousProcess.PaymentReason), "motivo_pagamento"), (nameof(ExercisePreviousProcess.EbRequest), "eb_requerimento"),
        (nameof(ExercisePreviousProcess.EbInformation), "eb_info"), (nameof(ExercisePreviousProcess.RefersTo), "referente_a"),
        (nameof(ExercisePreviousProcess.RequestedValue), "valor_requerido"), (nameof(ExercisePreviousProcess.FormerOdName), "od_epoca_nome"),
        (nameof(ExercisePreviousProcess.FormerOdIdentity), "od_epoca_idt"), (nameof(ExercisePreviousProcess.FormerOdCpf), "od_epoca_cpf"),
        (nameof(ExercisePreviousProcess.CompanyCommander), "cmt_companhia"), (nameof(ExercisePreviousProcess.RepresentativeName), "representante_nome"),
        (nameof(ExercisePreviousProcess.RepresentativeCpf), "representante_cpf"), (nameof(ExercisePreviousProcess.RepresentativeIdentity), "representante_idt"),
        (nameof(ExercisePreviousProcess.BirthDate), "data_nascimento"), (nameof(ExercisePreviousProcess.EaIndicative), "ea_indicativo"),
        (nameof(ExercisePreviousProcess.PreviousExerciseType), "tipo_exercicio_anterior"), (nameof(ExercisePreviousProcess.HasJudicialPension), "possui_pensao_judiciaria"),
        (nameof(ExercisePreviousProcess.RegistrationFileResearch), "pesquisa_ficha_cadastro"), (nameof(ExercisePreviousProcess.FinancialFileResearch), "pesquisa_ficha_financeira"),
        (nameof(ExercisePreviousProcess.SiafiResearch), "pesquisa_levantamento_siafi"), (nameof(ExercisePreviousProcess.RemittanceDocument), "documento_remessa"),
        (nameof(ExercisePreviousProcess.CpexProtocol), "cpex_protocolo"), (nameof(ExercisePreviousProcess.CpexPrintPage), "cpex_pagina_impressao"),
        (nameof(ExercisePreviousProcess.CpexProtocolledAt), "cpex_protocolado_em"), (nameof(ExercisePreviousProcess.CpexStatus), "cpex_status"),
        (nameof(ExercisePreviousProcess.CpexNotes), "cpex_obs"), (nameof(ExercisePreviousProcess.Paid), "pago"),
        (nameof(ExercisePreviousProcess.PaidAt), "pago_em"), (nameof(ExercisePreviousProcess.PaidNotes), "pago_obs"),
        (nameof(ExercisePreviousProcess.Situation), "situacao"), (nameof(ExercisePreviousProcess.Bank), "banco"),
        (nameof(ExercisePreviousProcess.Agency), "agencia"), (nameof(ExercisePreviousProcess.Account), "conta"),
        (nameof(ExercisePreviousProcess.ProcessNumber), "num_processo"), (nameof(ExercisePreviousProcess.ProcessYear), "ano_processo"),
        (nameof(ExercisePreviousProcess.RequestDateInWords), "data_solicitacao_extenso"), (nameof(ExercisePreviousProcess.OrganizationName), "om_nome"),
        (nameof(ExercisePreviousProcess.MilitaryRegion), "rm"), (nameof(ExercisePreviousProcess.ManagementUnit), "ug"),
        (nameof(ExercisePreviousProcess.Codom), "codom"), (nameof(ExercisePreviousProcess.OdNameRank), "od_nome_posto"),
        (nameof(ExercisePreviousProcess.OdFunction), "od_funcao"), (nameof(ExercisePreviousProcess.PersonnelChiefNameRank), "chefe_pessoal_nome_posto"),
        (nameof(ExercisePreviousProcess.PersonnelChiefFunction), "chefe_pessoal_funcao"),
        (nameof(ExercisePreviousProcess.AdministrativeInspectorNameRank), "fiscal_adm_nome_posto"),
        (nameof(ExercisePreviousProcess.AdministrativeInspectorFunction), "fiscal_adm_funcao"), (nameof(ExercisePreviousProcess.CityState), "cidade_estado"),
        (nameof(ExercisePreviousProcess.RequestDate), "data_requerimento"), (nameof(ExercisePreviousProcess.BulletinNumber), "bi_numero"),
        (nameof(ExercisePreviousProcess.BulletinDate), "bi_data"), (nameof(ExercisePreviousProcess.DebtType), "especie_divida"),
        (nameof(ExercisePreviousProcess.PeriodStart), "periodo_inicio"), (nameof(ExercisePreviousProcess.PeriodEnd), "periodo_fim"),
        (nameof(ExercisePreviousProcess.UpdatedThrough), "atualizado_ate"),
        (nameof(ExercisePreviousProcess.RightMaterializationDocument), "doc_materializou"),
        (nameof(ExercisePreviousProcess.BulletinThatRecorded), "boletim_averbou"),
        (nameof(ExercisePreviousProcess.NonPaymentExplanation), "explicacao_nao_pagamento")
    ];

    public ExercisePreviousRepository(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    private SqliteConnection Open()
    {
        var cn = new SqliteConnection($"Data Source={_paths.DatabaseFile};Cache=Shared;Mode=ReadWriteCreate;Default Timeout=15");
        cn.Open();
        using var pragma = cn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();
        return cn;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS ea_processos (
            id INTEGER PRIMARY KEY AUTOINCREMENT, militar_id INTEGER,
            posto_grad TEXT, nome_completo TEXT, nome_guerra TEXT, prec_cp TEXT, idt TEXT, cpf TEXT,
            protocolo_geral TEXT, secao TEXT, assunto_num TEXT, assunto_texto TEXT, anexos_folhas TEXT,
            destinatario TEXT, objeto TEXT, telefone TEXT, motivo_pagamento TEXT,
            eb_requerimento TEXT, eb_info TEXT, referente_a TEXT, valor_requerido TEXT,
            od_epoca_nome TEXT, od_epoca_idt TEXT, od_epoca_cpf TEXT, cmt_companhia TEXT,
            representante_nome TEXT, representante_cpf TEXT, representante_idt TEXT, data_nascimento TEXT,
            ea_indicativo TEXT, tipo_exercicio_anterior TEXT, possui_pensao_judiciaria TEXT,
            pesquisa_ficha_cadastro TEXT, pesquisa_ficha_financeira TEXT, pesquisa_levantamento_siafi TEXT,
            documento_remessa TEXT, cpex_protocolo TEXT, cpex_pagina_impressao TEXT,
            cpex_protocolado_em TEXT, cpex_status TEXT, cpex_obs TEXT,
            pago INTEGER NOT NULL DEFAULT 0, pago_em TEXT, pago_obs TEXT,
            situacao TEXT, banco TEXT, agencia TEXT, conta TEXT,
            num_processo TEXT, ano_processo INTEGER, data_solicitacao_extenso TEXT,
            om_nome TEXT, rm TEXT, ug TEXT, codom TEXT, od_nome_posto TEXT, od_funcao TEXT,
            chefe_pessoal_nome_posto TEXT, chefe_pessoal_funcao TEXT,
            fiscal_adm_nome_posto TEXT, fiscal_adm_funcao TEXT, cidade_estado TEXT,
            data_requerimento TEXT, bi_numero TEXT, bi_data TEXT, especie_divida TEXT,
            periodo_inicio TEXT NOT NULL DEFAULT '', periodo_fim TEXT NOT NULL DEFAULT '', atualizado_ate TEXT NOT NULL DEFAULT '',
            doc_materializou TEXT, boletim_averbou TEXT, explicacao_nao_pagamento TEXT,
            created_at TEXT NOT NULL DEFAULT (datetime('now')), updated_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        CREATE TABLE IF NOT EXISTS ea_codigos (
            id INTEGER PRIMARY KEY AUTOINCREMENT, processo_id INTEGER NOT NULL, ordem INTEGER NOT NULL,
            codigo_desc TEXT NOT NULL, tipo TEXT NOT NULL DEFAULT '-',
            FOREIGN KEY(processo_id) REFERENCES ea_processos(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS ea_lancamentos (
            id INTEGER PRIMARY KEY AUTOINCREMENT, processo_id INTEGER NOT NULL, codigo_ordem INTEGER NOT NULL,
            ano INTEGER NOT NULL, mes INTEGER NOT NULL CHECK(mes BETWEEN 1 AND 12),
            recebido REAL NOT NULL DEFAULT 0, devido REAL NOT NULL DEFAULT 0,
            UNIQUE(processo_id, codigo_ordem, ano, mes),
            FOREIGN KEY(processo_id) REFERENCES ea_processos(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS ea_periodo_por_ano (
            id INTEGER PRIMARY KEY AUTOINCREMENT, processo_id INTEGER NOT NULL, ano INTEGER NOT NULL,
            valor REAL NOT NULL DEFAULT 0, UNIQUE(processo_id, ano),
            FOREIGN KEY(processo_id) REFERENCES ea_processos(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS ipca_e (competencia TEXT PRIMARY KEY, percentual REAL, fator REAL NOT NULL);
        CREATE TABLE IF NOT EXISTS ea_presets (
            id INTEGER PRIMARY KEY AUTOINCREMENT, field TEXT NOT NULL, value TEXT NOT NULL, UNIQUE(field, value)
        );
        """;
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // Banco vindo de versões antigas: acrescenta qualquer coluna nova sem apagar dados.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cols = cn.CreateCommand())
        {
            cols.CommandText = "PRAGMA table_info(ea_processos)";
            await using var reader = await cols.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(1));
        }
        var definitions = ProcessMap.ToDictionary(x => x.Column, x => x.Column switch
        {
            "militar_id" or "ano_processo" or "pago" => "INTEGER",
            _ => "TEXT"
        }, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in definitions.Where(x => !existing.Contains(x.Key)))
        {
            await using var alter = cn.CreateCommand();
            alter.CommandText = $"ALTER TABLE ea_processos ADD COLUMN {pair.Key} {pair.Value}";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        await SeedPresetsAsync(cn, cancellationToken);
    }

    private static async Task SeedPresetsAsync(SqliteConnection cn, CancellationToken ct)
    {
        var groups = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["situacao"] = ExercisePreviousDefaults.Situations,
            ["ea_indicativo"] = ExercisePreviousDefaults.Indicatives,
            ["possui_pensao_judiciaria"] = ExercisePreviousDefaults.YesNo,
            ["pesquisa_ficha_cadastro"] = ["Sim", "Não"],
            ["pesquisa_ficha_financeira"] = ["Sim", "Não"],
            ["pesquisa_levantamento_siafi"] = ["Sim", "Não"],
            ["tipo_exercicio_anterior"] = ExercisePreviousDefaults.PreviousExerciseTypes
        };
        foreach (var group in groups)
        foreach (var value in group.Value)
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO ea_presets(field,value) VALUES($field,$value)";
            cmd.Parameters.AddWithValue("$field", group.Key);
            cmd.Parameters.AddWithValue("$value", value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<List<ExercisePreviousProcess>> ListAsync(bool? paid = null, int limit = 500, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM ea_processos {(paid.HasValue ? "WHERE COALESCE(pago,0)=$paid" : string.Empty)} ORDER BY COALESCE(pago,0), id DESC LIMIT $limit";
        if (paid.HasValue) cmd.Parameters.AddWithValue("$paid", paid.Value ? 1 : 0);
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<ExercisePreviousProcess>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadProcess(reader));
        return result;
    }

    public async Task<ExercisePreviousProcess?> GetAsync(int id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT * FROM ea_processos WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var p = ReadProcess(reader);
        await reader.DisposeAsync();
        foreach (var code in await GetCodesAsync(cn, id, ct)) p.Codes.Add(code);
        foreach (var entry in await GetEntriesAsync(cn, id, ct)) p.Entries.Add(entry);
        return p;
    }

    public async Task<ExercisePreviousProcess?> GetLatestForMilitaryAsync(int militaryId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT id FROM ea_processos WHERE militar_id=$militar ORDER BY COALESCE(updated_at,created_at) DESC,id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$militar", militaryId);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : await GetAsync(Convert.ToInt32(value, CultureInfo.InvariantCulture), ct);
    }

    public async Task<int> SaveAsync(ExercisePreviousProcess process, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        Normalize(process);
        await using var cn = Open();
        await using var transaction = await cn.BeginTransactionAsync(ct);
        await SaveProcessCoreAsync(cn, (SqliteTransaction)transaction, process, ct);
        await transaction.CommitAsync(ct);
        return process.Id;
    }

    public async Task<int> SaveImportedProcessesAsync(IEnumerable<ExercisePreviousProcess> processes, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        var list = processes.Where(x => x is not null).ToList();
        if (list.Count == 0) return 0;
        await using var cn = Open();
        await using var transaction = await cn.BeginTransactionAsync(ct);
        try
        {
            foreach (var process in list)
            {
                Normalize(process);
                await SaveProcessCoreAsync(cn, (SqliteTransaction)transaction, process, ct);
            }
            await transaction.CommitAsync(ct);
            return list.Count;
        }
        catch (Exception ex)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            await _log.WriteAsync("Importacao EA revertida por falha ao gravar lote.", ex);
            throw;
        }
    }

    private static async Task SaveProcessCoreAsync(SqliteConnection cn, SqliteTransaction transaction, ExercisePreviousProcess process, CancellationToken ct)
    {
        var properties = typeof(ExercisePreviousProcess).GetProperties().ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = transaction;
        var isNew = process.Id <= 0;
        if (isNew)
        {
            var columns = string.Join(",", ProcessMap.Select(x => x.Column));
            var parameters = string.Join(",", ProcessMap.Select(x => "$" + x.Column));
            cmd.CommandText = $"INSERT INTO ea_processos({columns}) VALUES({parameters})";
        }
        else
        {
            var set = string.Join(",", ProcessMap.Select(x => x.Column + "=$" + x.Column));
            cmd.CommandText = $"UPDATE ea_processos SET {set}, updated_at=datetime('now') WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", process.Id);
        }
        foreach (var (propertyName, column) in ProcessMap)
        {
            var value = properties[propertyName].GetValue(process);
            if (value is bool boolean) value = boolean ? 1 : 0;
            cmd.Parameters.AddWithValue("$" + column, value ?? DBNull.Value);
        }
        await cmd.ExecuteNonQueryAsync(ct);
        if (isNew)
        {
            await using var idCommand = cn.CreateCommand();
            idCommand.Transaction = transaction;
            idCommand.CommandText = "SELECT last_insert_rowid()";
            process.Id = Convert.ToInt32(await idCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        await SaveCodesCoreAsync(cn, transaction, process.Id, process.Codes, ct);
        await SaveEntriesCoreAsync(cn, transaction, process.Id, process.Entries, ct);
    }

    public async Task<int> DuplicateAsync(int id, CancellationToken ct = default)
    {
        var source = await GetAsync(id, ct) ?? throw new InvalidOperationException("Processo EA não encontrado.");
        var sourceId = source.Id;
        source.Id = 0;
        source.Paid = false;
        source.PaidAt = source.PaidNotes = string.Empty;
        source.CpexProtocol = source.CpexPrintPage = source.CpexProtocolledAt = source.CpexStatus = source.CpexNotes = string.Empty;
        var newId = await SaveAsync(source, ct);

        // A tabela por ano deixou de ser exibida nas versões novas, mas ainda pode
        // conter dados históricos criados pelo módulo Python. A duplicação preserva
        // esses registros para não descartar informação legada silenciosamente.
        await using var cn = Open();
        await using var copy = cn.CreateCommand();
        copy.CommandText = """
            INSERT OR IGNORE INTO ea_periodo_por_ano(processo_id,ano,valor)
            SELECT $newId,ano,valor FROM ea_periodo_por_ano WHERE processo_id=$sourceId
            """;
        copy.Parameters.AddWithValue("$newId", newId);
        copy.Parameters.AddWithValue("$sourceId", sourceId);
        await copy.ExecuteNonQueryAsync(ct);
        return newId;
    }

    public async Task SetPaidAsync(int id, bool paid, string notes, CancellationToken ct = default)
    {
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "UPDATE ea_processos SET pago=$pago,pago_em=$em,pago_obs=$obs,updated_at=datetime('now') WHERE id=$id";
        cmd.Parameters.AddWithValue("$pago", paid ? 1 : 0);
        cmd.Parameters.AddWithValue("$em", paid ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : DBNull.Value);
        cmd.Parameters.AddWithValue("$obs", paid ? notes ?? string.Empty : string.Empty);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "DELETE FROM ea_processos WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ExercisePreviousMilitarySearchResult>> SearchMilitaryAsync(string query, int limit = 80, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var cn = Open();
        var all = new List<ExercisePreviousMilitarySearchResult>();
        if (await TableExistsAsync(cn, "militares", ct))
        {
            var activeColumns = await GetTableColumnsAsync(cn, "militares", ct);
            var hasContacts = await TableExistsAsync(cn, "militares_contato", ct);
            var activeBirthDate = activeColumns.Contains("data_nascimento") ? "m.data_nascimento" : "''";
            var activePhone = hasContacts ? "COALESCE(c.telefone,'')" : "''";
            await ReadMilitarySearchRowsAsync(cn,
                $"SELECT 'M:'||m.id token,'Ativo' origem,m.id ativo_id,m.posto posto_grad,m.nome nome_completo,m.nome_guerra,m.cpf,m.prec_cp,m.idt,{activeBirthDate} data_nascimento,{activePhone} telefone,m.banco,m.agencia,m.conta FROM militares m" +
                (hasContacts ? " LEFT JOIN militares_contato c ON c.militar_id=m.id" : string.Empty),
                all, ct);
        }
        if (await TableExistsAsync(cn, "lt_militares", ct))
        {
            var ltColumns = await GetTableColumnsAsync(cn, "lt_militares", ct);
            var ltBirthDate = ltColumns.Contains("data_nascimento") ? "data_nascimento" : "''";
            var ltPhone = ltColumns.Contains("telefone") ? "telefone" : "''";
            await ReadMilitarySearchRowsAsync(cn,
                $"SELECT 'LT:'||id token,'Licenciado/Transferido' origem,NULL ativo_id,posto posto_grad,nome nome_completo,nome_guerra,cpf,prec_cp,idt,{ltBirthDate} data_nascimento,{ltPhone} telefone,banco,agencia,conta FROM lt_militares",
                all, ct);
        }
        if (all.Count == 0) return [];

        var text = NormalizeSearchText(query);
        var digits = Digits(query);
        var key = LettersAndDigits(query);
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in all)
            ScoreMilitarySearch(item, text, digits, key, tokens);

        return all
            .Where(x => string.IsNullOrWhiteSpace(text) || x.Confidence > 0)
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.Source.Equals("Ativo", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Rank, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    private static async Task ReadMilitarySearchRowsAsync(SqliteConnection cn, string sql, List<ExercisePreviousMilitarySearchResult> result, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ExercisePreviousMilitarySearchResult
            {
                Token = GetString(reader, "token"), Source = GetString(reader, "origem"),
                ActiveMilitaryId = reader["ativo_id"] is DBNull ? null : Convert.ToInt32(reader["ativo_id"], CultureInfo.InvariantCulture),
                Rank = CleanDbText(GetString(reader, "posto_grad")), FullName = CleanDbText(GetString(reader, "nome_completo")), WarName = CleanDbText(GetString(reader, "nome_guerra")),
                Cpf = FormatCpf(GetString(reader, "cpf")), PrecCp = CleanDbText(GetString(reader, "prec_cp")), Identity = CleanDbText(GetString(reader, "idt")),
                BirthDate = CleanDbText(GetString(reader, "data_nascimento")), Phone = CleanDbText(GetString(reader, "telefone")),
                Bank = GetString(reader, "banco"), Agency = GetString(reader, "agencia"), Account = GetString(reader, "conta")
            });
        }
    }

    private static void ScoreMilitarySearch(ExercisePreviousMilitarySearchResult item, string queryText, string queryDigits, string queryKey, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            item.Confidence = 1;
            item.MatchKind = "Lista";
            return;
        }

        var cpf = Digits(item.Cpf);
        var prec = LettersAndDigits(item.PrecCp);
        var identity = LettersAndDigits(item.Identity);
        var fullName = NormalizeSearchText(item.FullName);
        var warName = NormalizeSearchText(item.WarName);
        var rank = NormalizeSearchText(item.Rank);
        Set(0, string.Empty, false);

        // Busca por CPF/Prec-CP tem prioridade sobre nome.
        if (queryDigits.Length >= 11 && cpf.Length == 11 && queryDigits[^11..] == cpf) Set(100, "CPF exato", false);
        if (!string.IsNullOrWhiteSpace(queryKey) && !string.IsNullOrWhiteSpace(prec) && queryKey == prec) Set(98, "Prec-CP exato", false);
        if (!string.IsNullOrWhiteSpace(queryKey) && !string.IsNullOrWhiteSpace(identity) && queryKey == identity) Set(93, "Identidade exata", false);
        if (!string.IsNullOrWhiteSpace(queryText) && queryText == fullName) Set(90, "Nome completo exato", false);

        if (queryDigits.Length >= 4)
        {
            if (cpf.Contains(queryDigits, StringComparison.Ordinal)) Set(78, "CPF parcial", false);
            if (Digits(item.PrecCp).Contains(queryDigits, StringComparison.Ordinal)) Set(76, "Prec-CP parcial", false);
            if (Digits(item.Identity).Contains(queryDigits, StringComparison.Ordinal)) Set(72, "Identidade parcial", false);
        }

        if (tokens.Count > 0 && tokens.All(t => fullName.Contains(t, StringComparison.Ordinal)))
            Set(tokens.Count >= 2 ? 68 : 45, tokens.Count >= 2 ? "Nome completo parcial" : "Nome parcial", false);
        if (!string.IsNullOrWhiteSpace(fullName) && (fullName.Contains(queryText, StringComparison.Ordinal) || queryText.Contains(fullName, StringComparison.Ordinal)))
            Set(65, "Nome completo parcial", false);
        if (rank.Contains(queryText, StringComparison.Ordinal))
            Set(35, "Posto/graduação", false);

        // Nome de guerra isolado não confirma identidade.
        if (!string.IsNullOrWhiteSpace(warName) && queryText == warName) Set(30, "Nome de guerra (sugestão)", true);
        else if (!string.IsNullOrWhiteSpace(warName) && tokens.Any(t => warName.Contains(t, StringComparison.Ordinal))) Set(24, "Nome de guerra (sugestão)", true);

        void Set(int confidence, string kind, bool weak)
        {
            if (confidence <= item.Confidence) return;
            item.Confidence = confidence;
            item.MatchKind = kind;
            item.WeakMatch = weak;
        }
    }

    public async Task<List<string>> GetPresetsAsync(string field, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT value FROM ea_presets WHERE field=$field ORDER BY value COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$field", field);
        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) values.Add(reader.GetString(0));
        return values;
    }

    public async Task AddPresetAsync(string field, string value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO ea_presets(field,value) VALUES($field,$value)";
        cmd.Parameters.AddWithValue("$field", field);
        cmd.Parameters.AddWithValue("$value", value.Trim());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<decimal> GetIpcaFactorAsync(string competence, CancellationToken ct = default)
    {
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT fator FROM ipca_e WHERE competencia=$c";
        cmd.Parameters.AddWithValue("$c", competence);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 1m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    public async Task UpsertIpcaAsync(string competence, double? percentage, double factor, CancellationToken ct = default)
    {
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT INTO ipca_e(competencia,percentual,fator) VALUES($c,$p,$f) ON CONFLICT(competencia) DO UPDATE SET percentual=excluded.percentual,fator=excluded.fator";
        cmd.Parameters.AddWithValue("$c", competence);
        cmd.Parameters.AddWithValue("$p", percentage.HasValue ? percentage.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$f", factor);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string> GetMaxIpcaCompetenceAsync(CancellationToken ct = default)
    {
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(competencia),'') FROM ipca_e";
        return Convert.ToString(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public async Task<List<ExercisePreviousIpcaRow>> ListIpcaAsync(string start = "2000-01", string end = "2099-12", CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT competencia,percentual,fator FROM ipca_e WHERE competencia BETWEEN $start AND $end ORDER BY competencia";
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$end", end);
        var result = new List<ExercisePreviousIpcaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ExercisePreviousIpcaRow
            {
                Competence = reader.GetString(0),
                Percentage = reader.IsDBNull(1) ? null : reader.GetDouble(1),
                Factor = reader.GetDouble(2)
            });
        }
        return result;
    }

    public async Task DeleteIpcaAsync(string competence, CancellationToken ct = default)
    {
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "DELETE FROM ipca_e WHERE competencia=$c";
        cmd.Parameters.AddWithValue("$c", competence ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeletePresetAsync(string field, string value, CancellationToken ct = default)
    {
        await using var cn = Open();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "DELETE FROM ea_presets WHERE field=$field AND value=$value";
        cmd.Parameters.AddWithValue("$field", field ?? string.Empty);
        cmd.Parameters.AddWithValue("$value", value ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<string>> ListBanksAsync(CancellationToken ct = default)
    {
        await using var cn = Open();
        if (!await TableExistsAsync(cn, "bancos", ct)) return [];
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT nome FROM bancos WHERE TRIM(COALESCE(nome,''))<>'' ORDER BY nome COLLATE NOCASE";
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<ExercisePreviousSummary> CalculateSummaryAsync(ExercisePreviousProcess process, CancellationToken ct = default)
    {
        var summary = new ExercisePreviousSummary();
        foreach (var entry in process.Entries)
        {
            entry.Factor = await GetIpcaFactorAsync(entry.Competence, ct);
            summary.Received += entry.Received;
            summary.Due += entry.Due;
            summary.Net += entry.Net;
            summary.CorrectedReceived += entry.CorrectedReceived;
            summary.CorrectedDue += entry.CorrectedDue;
            summary.CorrectedNet += entry.CorrectedNet;
        }
        return summary;
    }

    private static async Task<List<ExercisePreviousCode>> GetCodesAsync(SqliteConnection cn, int processId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT ordem,codigo_desc,tipo FROM ea_codigos WHERE processo_id=$id ORDER BY ordem";
        cmd.Parameters.AddWithValue("$id", processId);
        var list = new List<ExercisePreviousCode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new ExercisePreviousCode { Order = reader.GetInt32(0), Description = reader.GetString(1), Type = reader.GetString(2) });
        return list;
    }

    private async Task<List<ExercisePreviousEntry>> GetEntriesAsync(SqliteConnection cn, int processId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT id,codigo_ordem,ano,mes,recebido,devido FROM ea_lancamentos WHERE processo_id=$id ORDER BY ano,mes,codigo_ordem";
        cmd.Parameters.AddWithValue("$id", processId);
        var list = new List<ExercisePreviousEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = new ExercisePreviousEntry
            {
                Id = reader.GetInt32(0), ProcessId = processId, CodeOrder = reader.GetInt32(1), Year = reader.GetInt32(2), Month = reader.GetInt32(3),
                Received = Convert.ToDecimal(reader.GetDouble(4), CultureInfo.InvariantCulture), Due = Convert.ToDecimal(reader.GetDouble(5), CultureInfo.InvariantCulture)
            };
            item.Factor = await GetIpcaFactorAsync(item.Competence, ct);
            list.Add(item);
        }
        return list;
    }

    private static async Task SaveCodesCoreAsync(SqliteConnection cn, SqliteTransaction tx, int id, IEnumerable<ExercisePreviousCode> codes, CancellationToken ct)
    {
        await using (var delete = cn.CreateCommand())
        {
            delete.Transaction = tx; delete.CommandText = "DELETE FROM ea_codigos WHERE processo_id=$id"; delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(ct);
        }
        foreach (var code in codes.OrderBy(x => x.Order).Take(17))
        {
            await using var insert = cn.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO ea_codigos(processo_id,ordem,codigo_desc,tipo) VALUES($id,$ord,$desc,$tipo)";
            insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$ord", code.Order);
            insert.Parameters.AddWithValue("$desc", code.Description ?? string.Empty);
            insert.Parameters.AddWithValue("$tipo", code.Type is "Receita" or "Despesa" ? code.Type : "-");
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task SaveEntriesCoreAsync(SqliteConnection cn, SqliteTransaction tx, int id, IEnumerable<ExercisePreviousEntry> entries, CancellationToken ct)
    {
        await using (var delete = cn.CreateCommand())
        {
            delete.Transaction = tx; delete.CommandText = "DELETE FROM ea_lancamentos WHERE processo_id=$id"; delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(ct);
        }
        var normalizedEntries = entries
            .Where(x => x.Month is >= 1 and <= 12 && x.CodeOrder is >= 1 and <= 17)
            .GroupBy(x => (x.CodeOrder, x.Year, x.Month))
            .Select(x => x.Last());

        foreach (var entry in normalizedEntries)
        {
            await using var insert = cn.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO ea_lancamentos(processo_id,codigo_ordem,ano,mes,recebido,devido) VALUES($id,$ord,$ano,$mes,$rec,$dev)";
            insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$ord", entry.CodeOrder);
            insert.Parameters.AddWithValue("$ano", entry.Year); insert.Parameters.AddWithValue("$mes", entry.Month);
            insert.Parameters.AddWithValue("$rec", Convert.ToDouble(entry.Received, CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$dev", Convert.ToDouble(entry.Due, CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static ExercisePreviousProcess ReadProcess(SqliteDataReader r)
    {
        var p = new ExercisePreviousProcess { Id = Convert.ToInt32(r["id"], CultureInfo.InvariantCulture) };
        var properties = typeof(ExercisePreviousProcess).GetProperties().ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (propertyName, column) in ProcessMap)
        {
            if (!HasColumn(r, column) || r[column] is DBNull) continue;
            var property = properties[propertyName];
            object? value;
            if (property.PropertyType == typeof(bool)) value = Convert.ToInt32(r[column], CultureInfo.InvariantCulture) != 0;
            else if (property.PropertyType == typeof(int?)) value = Convert.ToInt32(r[column], CultureInfo.InvariantCulture);
            else value = Convert.ToString(r[column], CultureInfo.InvariantCulture) ?? string.Empty;
            property.SetValue(p, value);
        }
        p.CreatedAt = HasColumn(r, "created_at") ? GetString(r, "created_at") : string.Empty;
        p.UpdatedAt = HasColumn(r, "updated_at") ? GetString(r, "updated_at") : string.Empty;
        return p;
    }

    private static void Normalize(ExercisePreviousProcess p)
    {
        p.PrecCp = Digits(p.PrecCp);
        p.Identity = Digits(p.Identity);
        p.Cpf = FormatCpf(p.Cpf);
        p.BulletinNumber = ExtractBulletinNumber(p.BulletinNumber);
        p.PeriodStart = NormalizeIsoDate(p.PeriodStart);
        p.PeriodEnd = NormalizeIsoDate(p.PeriodEnd);
        if (string.IsNullOrWhiteSpace(p.UpdatedThrough)) p.UpdatedThrough = DateTime.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(p.Situation)) p.Situation = "ATIVO";
        if (string.IsNullOrWhiteSpace(p.HasJudicialPension)) p.HasJudicialPension = "Não";
        p.RegistrationFileResearch = "Sim"; p.FinancialFileResearch = "Sim"; p.SiafiResearch = "Sim";
    }

    public static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
    public static string LettersAndDigits(string? value)
        => new(RemoveDiacritics(value ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string NormalizeSearchText(string? value)
    {
        var chars = RemoveDiacritics(value ?? string.Empty)
            .ToUpperInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static string FormatCpf(string? value)
    {
        var text = CleanDbText(value);
        var decimalZero = System.Text.RegularExpressions.Regex.Match(text, @"^\s*(\d{1,11})(?:[,.]0+)?\s*$");
        if (decimalZero.Success) text = decimalZero.Groups[1].Value;
        var digits = Digits(text);
        if (digits.Length == 10) digits = digits.PadLeft(11, '0');
        return digits.Length == 11 && digits.Any(ch => ch != '0')
            ? $"{digits[..3]}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits[9..]}"
            : text;
    }

    public static string CleanDbText(string? value)
    {
        var text = (value ?? string.Empty)
            .Replace('\u00a0', ' ')
            .Replace('\u200b', ' ')
            .Replace('\u200c', ' ')
            .Replace('\u200d', ' ')
            .Trim();
        if (text.Equals("nan", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("none", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
    }

    private static string RemoveDiacritics(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Normalize(NormalizationForm.FormC);
    }

    public static string ExtractBulletinNumber(string? value)
    {
        var text = value ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?:boletim\s+interno|\bbi\b|\badt\b|aditamento|nr|n[ºo°]|n\.)\D{0,20}(\d{1,6})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) match = System.Text.RegularExpressions.Regex.Match(text, @"\b(\d{1,6})\b");
        return match.Success ? match.Groups[1].Value : text.Trim();
    }
    public static string NormalizeIsoDate(string? value)
    {
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy" };
        return DateTime.TryParseExact(value?.Trim(), formats, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var dt)
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : value?.Trim() ?? string.Empty;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection cn, string table, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        cmd.Parameters.AddWithValue("$name", table);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
    private static async Task<HashSet<string>> GetTableColumnsAsync(SqliteConnection cn, string table, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(Convert.ToString(reader["name"], CultureInfo.InvariantCulture) ?? string.Empty);
        return result;
    }
    private static bool HasColumn(SqliteDataReader reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++) if (reader.GetName(i).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    private static string GetString(SqliteDataReader reader, string column) => reader[column] is DBNull ? string.Empty : Convert.ToString(reader[column], CultureInfo.InvariantCulture) ?? string.Empty;
}
