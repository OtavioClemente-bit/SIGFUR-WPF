using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class SalaryService
{
    public const string OfficialLawUrl = "https://www.planalto.gov.br/ccivil_03/_ato2023-2026/2025/lei/L15167.htm";

    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SalaryService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths;
        _json = json;
        _log = log;
    }

    public static IReadOnlyDictionary<string, decimal> Official2026 { get; } = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["General de Exército"] = 14711m,
        ["General de Divisão"] = 14100m,
        ["General de Brigada"] = 13639m,
        ["Coronel"] = 12505m,
        ["Tenente Coronel"] = 12285m,
        ["Major"] = 12108m,
        ["Capitão"] = 9976m,
        ["1º Tenente"] = 9004m,
        ["2º Tenente"] = 8179m,
        ["Aspirante"] = 7988m,
        ["Subtenente"] = 6737m,
        ["1º Sargento"] = 5988m,
        ["2º Sargento"] = 5209m,
        ["3º Sargento"] = 4177m,
        ["Cabo Efetivo Profissional"] = 2869m,
        ["Soldado Efetivo Profissional"] = 1927m,
        ["Soldado Efetivo Variável"] = 1177m
    };

    public async Task<List<SalaryRecord>> GetAllAsync(bool includeHidden = false, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var hidden = await LoadHiddenAsync();
        var rows = new List<SalaryRecord>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT posto, soldo FROM soldos_por_posto ORDER BY posto COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rank = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
            if (string.IsNullOrWhiteSpace(rank) || rank.Equals("Marechal", StringComparison.OrdinalIgnoreCase)) continue;
            var salary = reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            var canonical = MilitaryRankService.Canonicalize(rank);
            var isHidden = hidden.Contains(rank) || hidden.Contains(canonical);
            if (!includeHidden && isHidden) continue;
            rows.Add(new SalaryRecord
            {
                Rank = canonical,
                Salary = salary,
                Official2026 = ResolveOfficial(canonical),
                IsHidden = isHidden
            });
        }

        return rows
            .GroupBy(x => MilitaryRankService.Canonicalize(x.Rank), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Salary > 0).ThenByDescending(x => x.Salary).First())
            .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.Rank, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task SaveAsync(string rank, decimal salary, CancellationToken cancellationToken = default)
    {
        rank = MilitaryRankService.Canonicalize((rank ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(rank)) throw new InvalidOperationException("Informe o posto ou a graduação.");
        if (salary < 0) throw new InvalidOperationException("O soldo não pode ser negativo.");
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO soldos_por_posto(posto,soldo) VALUES($posto,$soldo) ON CONFLICT(posto) DO UPDATE SET soldo=excluded.soldo;";
        command.Parameters.AddWithValue("$posto", rank);
        command.Parameters.AddWithValue("$soldo", salary);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SetHiddenAsync(rank, false, cancellationToken);
    }

    public async Task SetHiddenAsync(string rank, bool hidden, CancellationToken cancellationToken = default)
    {
        rank = (rank ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rank)) return;
        var store = await _json.LoadAsync<SalaryHiddenStore>(_paths.SalaryHiddenFile) ?? new SalaryHiddenStore();
        var values = new HashSet<string>(store.Hidden ?? [], StringComparer.OrdinalIgnoreCase);
        if (hidden) values.Add(rank); else values.RemoveWhere(x => x.Equals(rank, StringComparison.OrdinalIgnoreCase) || MilitaryRankService.Canonicalize(x).Equals(MilitaryRankService.Canonicalize(rank), StringComparison.OrdinalIgnoreCase));
        store.Hidden = values.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
        await _json.SaveAsync(_paths.SalaryHiddenFile, store);
    }

    public async Task RestoreAllHiddenAsync(CancellationToken cancellationToken = default)
        => await _json.SaveAsync(_paths.SalaryHiddenFile, new SalaryHiddenStore());

    public async Task<int> ApplyOfficial2026Async(bool overwriteExisting, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var updated = 0;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var pair in Official2026)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = overwriteExisting
                ? "INSERT INTO soldos_por_posto(posto,soldo) VALUES($posto,$soldo) ON CONFLICT(posto) DO UPDATE SET soldo=excluded.soldo;"
                : "INSERT OR IGNORE INTO soldos_por_posto(posto,soldo) VALUES($posto,$soldo);";
            command.Parameters.AddWithValue("$posto", pair.Key);
            command.Parameters.AddWithValue("$soldo", pair.Value);
            updated += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
        return updated;
    }

    public async Task ExportCsvAsync(string path, IEnumerable<SalaryRecord> records, CancellationToken cancellationToken = default)
    {
        static string Csv(string? text) => '"' + (text ?? string.Empty).Replace("\"", "\"\"") + '"';
        var lines = new List<string> { "POSTO/GRADUAÇÃO;ABREVIAÇÃO;SOLDO CONFIGURADO;REFERÊNCIA OFICIAL 2026;DIFERENÇA;STATUS" };
        foreach (var row in records)
        {
            lines.Add(string.Join(';',
                Csv(row.Rank), Csv(row.ShortRank),
                row.Salary.ToString("0.00", CultureInfo.InvariantCulture),
                row.Official2026.ToString("0.00", CultureInfo.InvariantCulture),
                row.Difference.ToString("0.00", CultureInfo.InvariantCulture),
                Csv(row.StatusText)));
        }
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true), cancellationToken);
    }

    public decimal ResolveOfficial(string? rank)
    {
        var canonical = MilitaryRankService.Canonicalize(rank);
        if (Official2026.TryGetValue(canonical, out var value)) return value;
        var normalized = MilitaryRankService.Normalize(rank);
        foreach (var pair in Official2026)
            if (MilitaryRankService.Normalize(pair.Key).Equals(normalized, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return 0m;
    }

    private async Task<HashSet<string>> LoadHiddenAsync()
    {
        var store = await _json.LoadAsync<SalaryHiddenStore>(_paths.SalaryHiddenFile) ?? new SalaryHiddenStore();
        return new HashSet<string>(store.Hidden ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS soldos_por_posto(posto TEXT PRIMARY KEY, soldo REAL NOT NULL DEFAULT 0);";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await NormalizeLegacyRanksAsync(connection, cancellationToken);
            await CorrectKnownSoldierDefaultsAsync(connection, cancellationToken);
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM soldos_por_posto;";
            var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (count == 0)
            {
                foreach (var pair in Official2026)
                {
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT OR IGNORE INTO soldos_por_posto(posto,soldo) VALUES($posto,$soldo);";
                    insert.Parameters.AddWithValue("$posto", pair.Key);
                    insert.Parameters.AddWithValue("$soldo", pair.Value);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao preparar a tabela de soldos nativa.", ex);
            throw;
        }
        finally { _gate.Release(); }
    }


    private static async Task NormalizeLegacyRanksAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cabo"] = "Cabo Efetivo Profissional",
            ["Cb"] = "Cabo Efetivo Profissional",
            ["Cabo Efetivo Variável"] = "Cabo Efetivo Profissional",
            ["Cb Ef Vrv"] = "Cabo Efetivo Profissional",
            ["Cb EV"] = "Cabo Efetivo Profissional",
            ["Soldado"] = "Soldado Efetivo Profissional",
            ["Sd"] = "Soldado Efetivo Profissional",
            ["Soldado do Exército"] = "Soldado Efetivo Profissional",
            ["Soldado Engajado"] = "Soldado Efetivo Profissional",
            ["Sd Ef Profl"] = "Soldado Efetivo Profissional",
            ["Sd EP"] = "Soldado Efetivo Profissional",
            ["Soldado Antigo"] = "Soldado Efetivo Profissional",
            ["Sd Antigo"] = "Soldado Efetivo Profissional",
            ["Recruta"] = "Soldado Efetivo Variável",
            ["Soldado-Recruta"] = "Soldado Efetivo Variável",
            ["Soldado Recruta"] = "Soldado Efetivo Variável",
            ["Sd Rcr"] = "Soldado Efetivo Variável",
            ["Sd Ef Vrv"] = "Soldado Efetivo Variável",
            ["Sd EV"] = "Soldado Efetivo Variável"
        };

        foreach (var pair in aliases)
        {
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT soldo FROM soldos_por_posto WHERE posto=$posto COLLATE NOCASE LIMIT 1;";
            read.Parameters.AddWithValue("$posto", pair.Key);
            var value = await read.ExecuteScalarAsync(cancellationToken);
            if (value is null || value is DBNull) continue;
            var salary = Convert.ToDecimal(value, CultureInfo.InvariantCulture);

            await using var upsert = connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO soldos_por_posto(posto,soldo) VALUES($canonical,$soldo)
                ON CONFLICT(posto) DO UPDATE SET soldo = CASE
                    WHEN soldos_por_posto.soldo <= 0 THEN excluded.soldo
                    ELSE soldos_por_posto.soldo END;
                """;
            upsert.Parameters.AddWithValue("$canonical", pair.Value);
            upsert.Parameters.AddWithValue("$soldo", salary);
            await upsert.ExecuteNonQueryAsync(cancellationToken);

            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM soldos_por_posto WHERE posto=$posto COLLATE NOCASE AND posto<>$canonical;";
            delete.Parameters.AddWithValue("$posto", pair.Key);
            delete.Parameters.AddWithValue("$canonical", pair.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }


    private static async Task CorrectKnownSoldierDefaultsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Corrige somente valores padrão incorretos distribuídos por versões anteriores.
        // Valores personalizados diferentes destes não são sobrescritos.
        var corrections = new[]
        {
            (Rank: "Soldado Efetivo Profissional", OldValues: new[] { 2103m, 4800m }, Correct: 1927m),
            (Rank: "Soldado Efetivo Variável", OldValues: new[] { 1927m, 3800m }, Correct: 1177m)
        };

        foreach (var correction in corrections)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE soldos_por_posto
                   SET soldo=$correct
                 WHERE posto=$rank COLLATE NOCASE
                   AND (ABS(soldo-$old1)<0.001 OR ABS(soldo-$old2)<0.001 OR soldo<=0);
                """;
            command.Parameters.AddWithValue("$correct", correction.Correct);
            command.Parameters.AddWithValue("$rank", correction.Rank);
            command.Parameters.AddWithValue("$old1", correction.OldValues[0]);
            command.Parameters.AddWithValue("$old2", correction.OldValues[1]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private SqliteConnection OpenConnection()
        => new(new SqliteConnectionStringBuilder { DataSource = _paths.DatabaseFile, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString());
}
