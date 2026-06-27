using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Base local de endereços de Organizações Militares utilizada nos documentos dos Correios.
/// A tabela e o fluxo seguem a versão Python: pesquisa local, inclusão/atualização manual e
/// sincronização deliberada com a página oficial "Quartéis por Estado".
/// </summary>
public sealed class PostalOmAddressService
{
    public const string OfficialSourceUrl = "https://www.eb.mil.br/quarteis-por-estado";
    private readonly string _databasePath;
    private readonly LogService _log;
    private static readonly HttpClient Http = CreateClient();

    public PostalOmAddressService(AppPaths paths, LogService log)
    {
        _databasePath = paths.PostalOmDatabaseFile;
        _log = log;
    }

    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS correios_oms (
                om_destino TEXT PRIMARY KEY COLLATE NOCASE,
                logradouro_destino TEXT,
                numero_destino TEXT,
                complemento_destino TEXT,
                bairro_destino TEXT,
                cidade_destino TEXT,
                uf_destino TEXT,
                cep_destino TEXT,
                fonte_url TEXT,
                fonte_nome TEXT,
                importado_online INTEGER NOT NULL DEFAULT 0,
                atualizado_em TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnsAsync(connection, cancellationToken);

        await UpsertCoreAsync(connection, new PostalOmAddress
        {
            OmName = "2º Batalhão Ferroviário - 2º B Fv",
            Street = "R. Profa. Lourdes Naves",
            Number = "750",
            Neighborhood = "Santo Antônio",
            City = "Araguari",
            State = "MG",
            ZipCode = "38440-000",
            SourceUrl = OfficialSourceUrl,
            SourceName = "Template inicial / Quartéis por Estado",
            ImportedOnline = false
        }, preserveExisting: true, cancellationToken);


        foreach (var alias in new[]
        {
            "4ª Companhia de Polícia do Exército",
            "4ª Cia PE",
            "4 Cia PE",
            "4ª Companhia PE",
            "4ª Cia de Polícia do Exército",
            "4 Cia de Policia do Exercito",
            "4ª Cia Polícia Exército",
            "4 Cia Policia Exercito",
            "4A Cia PE",
            "4A Companhia PE",
            "4A Companhia de Policia do Exercito"
        })
        {
            await UpsertCoreAsync(connection, new PostalOmAddress
            {
                OmName = alias,
                Street = "Rua Juiz de Fora",
                Number = "990",
                Neighborhood = "Barro Preto",
                City = "Belo Horizonte",
                State = "MG",
                ZipCode = "30180-060",
                SourceUrl = "Cadastro inicial SIGFUR",
                SourceName = "Atalho local 4ª Cia PE",
                ImportedOnline = false
            }, preserveExisting: true, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<PostalOmAddress>> SearchAsync(string? term = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(cancellationToken);
        var all = new List<PostalOmAddress>();
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT om_destino, logradouro_destino, numero_destino, complemento_destino,
                   bairro_destino, cidade_destino, uf_destino, cep_destino,
                   fonte_url, fonte_nome, importado_online
              FROM correios_oms
             ORDER BY om_destino COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) all.Add(Read(reader));

        var search = Normalize(term);
        return all
            .Select(item => (Item: item, Score: Rank(item, search)))
            .Where(x => string.IsNullOrWhiteSpace(search) || x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.OmName, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(x => x.Item)
            .ToList();
    }

    public async Task<PostalOmAddress?> FindByNameAsync(string? name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        await EnsureAsync(cancellationToken);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT om_destino, logradouro_destino, numero_destino, complemento_destino,
                   bairro_destino, cidade_destino, uf_destino, cep_destino,
                   fonte_url, fonte_nome, importado_online
              FROM correios_oms
             WHERE lower(om_destino)=lower($name)
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", name.Trim());
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken)) return Read(reader);

        // Busca profissional/fuzzy: se o usuário digitou "4 cia pe", "4ª companhia"
        // ou parte da cidade, retorna a OM mais provável em vez de falhar.
        return (await SearchAsync(name, 1, cancellationToken)).FirstOrDefault();
    }

    public async Task UpsertAsync(PostalOmAddress item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.OmName)) throw new InvalidOperationException("Informe o nome da OM.");
        await EnsureAsync(cancellationToken);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await UpsertCoreAsync(connection, item, preserveExisting: false, cancellationToken);
    }

    public async Task<int> SyncFromOfficialAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Acessando a página oficial de Quartéis por Estado...");
        string html;
        try
        {
            html = await Http.GetStringAsync(OfficialSourceUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao sincronizar a base de OMs dos Correios.", ex);
            throw new InvalidOperationException("Não foi possível acessar a página oficial de Quartéis por Estado. Confira a internet e tente novamente.", ex);
        }

        var items = ParseOfficialPage(html);
        if (items.Count == 0)
            throw new InvalidOperationException("A página oficial foi acessada, mas nenhum endereço de OM foi reconhecido. A estrutura do site pode ter mudado.");

        await EnsureAsync(cancellationToken);
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var count = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Salvando {count + 1}/{items.Count}: {item.OmName}");
            await UpsertCoreAsync(connection, item, preserveExisting: false, cancellationToken, transaction);
            count++;
        }
        transaction.Commit();
        return count;
    }

    public static string BuildDestinationAddress(PostalOmAddress item)
    {
        var line = (item.Street ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(item.Number)) line += $", {item.Number.Trim()}";
        if (!string.IsNullOrWhiteSpace(item.Complement)) line += $", {item.Complement.Trim()}";
        if (!string.IsNullOrWhiteSpace(item.Neighborhood)) line += $" - {item.Neighborhood.Trim()}";
        return line.Trim(' ', ',', '-');
    }

    public static string FormatZipCode(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return digits.Length == 8 ? $"{digits[..5]}-{digits[5..]}" : (value ?? string.Empty).Trim();
    }

    private SqliteConnection Open() => new($"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared");

    private static async Task EnsureColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(correios_oms);";
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(1));

        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fonte_url"] = "TEXT",
            ["fonte_nome"] = "TEXT",
            ["importado_online"] = "INTEGER NOT NULL DEFAULT 0",
            ["atualizado_em"] = "TEXT"
        };
        foreach (var column in required.Where(x => !existing.Contains(x.Key)))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE correios_oms ADD COLUMN {column.Key} {column.Value};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertCoreAsync(
        SqliteConnection connection,
        PostalOmAddress item,
        bool preserveExisting,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = preserveExisting
            ? """
              INSERT INTO correios_oms(
                  om_destino, logradouro_destino, numero_destino, complemento_destino,
                  bairro_destino, cidade_destino, uf_destino, cep_destino,
                  fonte_url, fonte_nome, importado_online, atualizado_em)
              VALUES($om,$street,$number,$complement,$neighborhood,$city,$state,$zip,$url,$source,$online,CURRENT_TIMESTAMP)
              ON CONFLICT(om_destino) DO NOTHING;
              """
            : """
              INSERT INTO correios_oms(
                  om_destino, logradouro_destino, numero_destino, complemento_destino,
                  bairro_destino, cidade_destino, uf_destino, cep_destino,
                  fonte_url, fonte_nome, importado_online, atualizado_em)
              VALUES($om,$street,$number,$complement,$neighborhood,$city,$state,$zip,$url,$source,$online,CURRENT_TIMESTAMP)
              ON CONFLICT(om_destino) DO UPDATE SET
                  logradouro_destino=excluded.logradouro_destino,
                  numero_destino=excluded.numero_destino,
                  complemento_destino=excluded.complemento_destino,
                  bairro_destino=excluded.bairro_destino,
                  cidade_destino=excluded.cidade_destino,
                  uf_destino=excluded.uf_destino,
                  cep_destino=excluded.cep_destino,
                  fonte_url=excluded.fonte_url,
                  fonte_nome=excluded.fonte_nome,
                  importado_online=excluded.importado_online,
                  atualizado_em=CURRENT_TIMESTAMP;
              """;
        command.Parameters.AddWithValue("$om", item.OmName.Trim());
        command.Parameters.AddWithValue("$street", item.Street?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$number", item.Number?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$complement", item.Complement?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$neighborhood", item.Neighborhood?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$city", item.City?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$state", item.State?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("$zip", FormatZipCode(item.ZipCode));
        command.Parameters.AddWithValue("$url", item.SourceUrl?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$source", item.SourceName?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$online", item.ImportedOnline ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PostalOmAddress Read(SqliteDataReader reader) => new()
    {
        OmName = reader.GetString(0),
        Street = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
        Number = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        Complement = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
        Neighborhood = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
        City = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
        State = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
        ZipCode = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
        SourceUrl = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
        SourceName = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
        ImportedOnline = !reader.IsDBNull(10) && reader.GetInt32(10) != 0
    };

    private static int Rank(PostalOmAddress item, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return 1;
        search = Normalize(AliasText(search));
        var name = Normalize(item.OmName);
        var alias = Normalize(AliasText(item.OmName));
        var city = Normalize(item.City);
        var state = Normalize(item.State);
        var zip = Normalize(item.ZipCode);
        var haystack = string.Join(' ', name, alias, city, state, zip);
        if (name == search || alias == search) return 800;
        if (name.StartsWith(search, StringComparison.Ordinal) || alias.StartsWith(search, StringComparison.Ordinal)) return 650;
        var score = haystack.Contains(search, StringComparison.Ordinal) ? 420 : 0;
        var tokens = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && tokens.All(token => haystack.Contains(token, StringComparison.Ordinal))) score += 260;
        score += tokens.Count(token => haystack.Contains(token, StringComparison.Ordinal)) * 45;
        if (tokens.Any(token => name.StartsWith(token, StringComparison.Ordinal) || alias.StartsWith(token, StringComparison.Ordinal))) score += 80;
        if (city.Contains(search, StringComparison.Ordinal)) score += 90;
        return score;
    }

    private static string AliasText(string? value)
    {
        var text = value ?? string.Empty;
        text = Regex.Replace(text, @"\b4\s*a\b", "4", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"companhia\s+de", "cia", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, "companhia", "cia", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, "pol[ií]cia do ex[eé]rcito", "pe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, "pol[ií]cia ex[eé]rcito", "pe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\bcia\s+de\s+pe\b", "cia pe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }

    private static List<PostalOmAddress> ParseOfficialPage(string html)
    {
        var text = HtmlToText(html);
        var pattern = new Regex(
            @"(?ms)(?:^|\n)(?:#+\s*)?(?<om>[^\n:]{4,}?)\n+\s*Endere[cç]o:\s*(?<end>[^\n]+)\n+\s*Bairro:\s*(?<bairro>[^\n]+)\n+\s*Cidade\s*-\s*UF:\s*(?<cidadeuf>[^\n]+)\n+\s*CEP:\s*(?<cep>[^\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var result = new List<PostalOmAddress>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in pattern.Matches(text))
        {
            var name = Collapse(match.Groups["om"].Value).Trim(' ', '-', '–', '—');
            if (name.Length < 4 || !seen.Add(Normalize(name))) continue;
            var (city, state) = ParseCityState(match.Groups["cidadeuf"].Value);
            var address = Collapse(match.Groups["end"].Value);
            if (!string.IsNullOrWhiteSpace(city) && !string.IsNullOrWhiteSpace(state))
                address = Regex.Replace(address, $@"\s+{Regex.Escape(city)}\s*-\s*{Regex.Escape(state)}\s*$", string.Empty, RegexOptions.IgnoreCase);
            var (street, number) = SplitStreetNumber(address);
            result.Add(new PostalOmAddress
            {
                OmName = name,
                Street = street,
                Number = number,
                Neighborhood = Collapse(match.Groups["bairro"].Value),
                City = city,
                State = state,
                ZipCode = FormatZipCode(match.Groups["cep"].Value),
                SourceUrl = OfficialSourceUrl,
                SourceName = "Quartéis por Estado",
                ImportedOnline = true
            });
        }
        return result;
    }

    private static string HtmlToText(string html)
    {
        var text = Regex.Replace(html ?? string.Empty, @"(?is)<script.*?>.*?</script>", " ");
        text = Regex.Replace(text, @"(?is)<style.*?>.*?</style>", " ");
        text = Regex.Replace(text, @"(?i)<br\s*/?>", "\n");
        text = Regex.Replace(text, @"(?i)</(p|div|section|article|li|tr|h1|h2|h3|h4|h5|h6|ul|ol|table|td|th)>", "\n");
        text = Regex.Replace(text, @"(?i)<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
        return string.Join("\n", text.Split('\n').Select(Collapse).Where(x => x.Length > 0));
    }

    private static (string Street, string Number) SplitStreetNumber(string value)
    {
        var text = Collapse(value).Trim(' ', ',', ';', '-', '.');
        var match = Regex.Match(text, @"^(.*?)(?:,?\s+(?:N[ºO°Rr./-]*\s*)?)((?:S/N|SNR|SN|\d+[A-Z0-9/-]*))$", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Value.Trim(' ', ',', ';', '-', '.'), match.Groups[2].Value.Trim()) : (text, string.Empty);
    }

    private static (string City, string State) ParseCityState(string value)
    {
        var text = Collapse(value);
        var match = Regex.Match(text, @"^(.*?)[\s\-–—]+([A-Z]{2})$", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Value.Trim(' ', '-', '–', '—'), match.Groups[2].Value.ToUpperInvariant()) : (text, string.Empty);
    }

    private static string Collapse(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var plain = new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(plain.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SIGFUR/5.0");
        return client;
    }
}
