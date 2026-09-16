using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly SemaphoreSlim _officialSourceGate = new(1, 1);
    private readonly Dictionary<string, OfficialSalarySnapshot> _officialSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient OfficialClient = CreateOfficialClient();

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

    public static IReadOnlyDictionary<string, decimal> OfficialUntilMarch2025 { get; } = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["General de Exército"] = 13471m, ["General de Divisão"] = 12912m, ["General de Brigada"] = 12490m,
        ["Coronel"] = 11451m, ["Tenente Coronel"] = 11250m, ["Major"] = 11088m, ["Capitão"] = 9135m,
        ["1º Tenente"] = 8245m, ["2º Tenente"] = 7490m, ["Aspirante"] = 7315m, ["Subtenente"] = 6169m,
        ["1º Sargento"] = 5483m, ["2º Sargento"] = 4770m, ["3º Sargento"] = 3825m,
        ["Cabo Efetivo Profissional"] = 2627m, ["Soldado Efetivo Profissional"] = 1765m, ["Soldado Efetivo Variável"] = 1078m
    };

    public static IReadOnlyDictionary<string, decimal> OfficialFromApril2025 { get; } = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
        ["General de Exército"] = 14077m, ["General de Divisão"] = 13493m, ["General de Brigada"] = 13052m,
        ["Coronel"] = 11966m, ["Tenente Coronel"] = 11756m, ["Major"] = 11587m, ["Capitão"] = 9546m,
        ["1º Tenente"] = 8616m, ["2º Tenente"] = 7827m, ["Aspirante"] = 7644m, ["Subtenente"] = 6447m,
        ["1º Sargento"] = 5730m, ["2º Sargento"] = 4985m, ["3º Sargento"] = 3997m,
        ["Cabo Efetivo Profissional"] = 2745m, ["Soldado Efetivo Profissional"] = 1844m, ["Soldado Efetivo Variável"] = 1127m
    };

    private static readonly IReadOnlyDictionary<string, string> OfficialRowNeedles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["General de Exército"] = "General de Exército", ["General de Divisão"] = "General de Divisão", ["General de Brigada"] = "General de Brigada",
        ["Coronel"] = "Capitão de Mar e Guerra e Coronel", ["Tenente Coronel"] = "Tenente-Coronel", ["Major"] = "Capitão de Corveta e Major",
        ["Capitão"] = "Capitão-Tenente e Capitão", ["1º Tenente"] = "Primeiro-Tenente", ["2º Tenente"] = "Segundo-Tenente",
        ["Aspirante"] = "Guarda-Marinha e Aspirante a Oficial", ["Subtenente"] = "Suboficial e Subtenente",
        ["1º Sargento"] = "Primeiro-Sargento", ["2º Sargento"] = "Segundo-Sargento", ["3º Sargento"] = "Terceiro-Sargento",
        ["Cabo Efetivo Profissional"] = "Cabo (engajado)",
        ["Soldado Efetivo Profissional"] = "Soldado do Exército e Soldado de Segunda Classe (engajado)",
        ["Soldado Efetivo Variável"] = "Marinheiro-Recruta, Recruta, Soldado"
    };

    public async Task<OfficialSalarySnapshot> GetOfficialSnapshotAsync(DateTime referenceDate, CancellationToken cancellationToken = default)
    {
        if (referenceDate.Year < 2020)
            throw new InvalidOperationException("A consulta histórica automática de soldos está disponível a partir de 2020.");

        var key = OfficialPeriodKey(referenceDate);
        await _officialSourceGate.WaitAsync(cancellationToken);
        try
        {
            if (_officialSnapshots.TryGetValue(key, out var cached))
                return new OfficialSalarySnapshot
                {
                    ReferenceDate = referenceDate,
                    Salaries = cached.Salaries,
                    EffectivePeriod = cached.EffectivePeriod,
                    LegalBasis = cached.LegalBasis,
                    SourceUrl = cached.SourceUrl,
                    RetrievedOnline = cached.RetrievedOnline
                };

            var embedded = new Dictionary<string, decimal>(EmbeddedOfficialTable(referenceDate), StringComparer.OrdinalIgnoreCase);
            var retrievedOnline = false;
            try
            {
                var html = await OfficialClient.GetStringAsync(OfficialLawUrl, cancellationToken);
                var parsed = ParseOfficialSalaryTable(html, referenceDate);
                if (parsed.Count >= 15)
                {
                    foreach (var pair in parsed) embedded[pair.Key] = pair.Value;
                    retrievedOnline = true;
                }
            }
            catch (Exception ex)
            {
                await _log.WriteAsync("Não foi possível atualizar a tabela histórica de soldos no portal oficial; será usada a referência legal incorporada ao SIGFUR.", ex);
            }

            var snapshot = new OfficialSalarySnapshot
            {
                ReferenceDate = referenceDate,
                Salaries = embedded,
                EffectivePeriod = DescribeOfficialPeriod(referenceDate),
                LegalBasis = "Anexo VI da Lei nº 13.954, de 16 DEZ 2019, com redação dada pela Lei nº 15.167, de 17 JUL 2025",
                SourceUrl = OfficialLawUrl,
                RetrievedOnline = retrievedOnline
            };
            _officialSnapshots[key] = snapshot;
            return snapshot;
        }
        finally
        {
            _officialSourceGate.Release();
        }
    }

    public static string DescribeOfficialPeriod(DateTime referenceDate)
        => referenceDate < new DateTime(2025, 4, 1)
            ? $"soldo vigente no ano-base {referenceDate.Year} (tabela aplicável até 31 MAR 2025)"
            : referenceDate < new DateTime(2026, 1, 1)
                ? "soldo vigente a partir de 1º ABR 2025"
                : "soldo vigente a partir de 1º JAN 2026";

    private static string OfficialPeriodKey(DateTime referenceDate)
        => referenceDate < new DateTime(2025, 4, 1) ? "ATE_2025_03" : referenceDate < new DateTime(2026, 1, 1) ? "DESDE_2025_04" : "DESDE_2026_01";

    private static IReadOnlyDictionary<string, decimal> EmbeddedOfficialTable(DateTime referenceDate)
        => referenceDate < new DateTime(2025, 4, 1) ? OfficialUntilMarch2025 : referenceDate < new DateTime(2026, 1, 1) ? OfficialFromApril2025 : Official2026;

    private static Dictionary<string, decimal> ParseOfficialSalaryTable(string html, DateTime referenceDate)
    {
        var column = referenceDate < new DateTime(2025, 4, 1) ? 0 : referenceDate < new DateTime(2026, 1, 1) ? 1 : 2;
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (Match rowMatch in Regex.Matches(html ?? string.Empty, @"<tr\b[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var rowHtml = rowMatch.Groups["row"].Value;
            var cells = Regex.Matches(rowHtml, @"<t[dh]\b[^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(match => CleanHtmlText(match.Groups["cell"].Value))
                .ToList();
            if (cells.Count < 4) continue;

            var rowText = string.Join(" ", cells);
            var normalizedRow = NormalizeOfficialText(rowText);
            var amounts = cells.SelectMany(cell => Regex.Matches(cell, @"\b\d{1,3}(?:\.\d{3})*,\d{2}\b").Select(match => match.Value))
                .Select(value => decimal.TryParse(value, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out var parsed) ? parsed : 0m)
                .Where(value => value > 0)
                .ToList();
            if (amounts.Count < 3) continue;

            foreach (var pair in OfficialRowNeedles)
            {
                if (!normalizedRow.Contains(NormalizeOfficialText(pair.Value), StringComparison.Ordinal)) continue;
                result[pair.Key] = amounts[amounts.Count - 3 + column];
            }
        }
        return result;
    }

    private static string CleanHtmlText(string value)
    {
        var withoutTags = Regex.Replace(value ?? string.Empty, @"<[^>]+>", " ", RegexOptions.Singleline);
        return Regex.Replace(WebUtility.HtmlDecode(withoutTags).Replace('\u00A0', ' '), @"\s+", " ").Trim();
    }

    private static string NormalizeOfficialText(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToUpperInvariant).ToArray());
    }

    private static HttpClient CreateOfficialClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SIGFUR/1.0 (consulta oficial de soldos)");
        return client;
    }

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
