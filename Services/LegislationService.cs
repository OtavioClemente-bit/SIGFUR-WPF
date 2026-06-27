using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SIGFUR.Wpf.Models;
using UglyToad.PdfPig;

namespace SIGFUR.Wpf.Services;

public sealed class LegislationService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Extensões que viram conteúdo real na biblioteca da IA.
        // A IA/indexador NÃO usa .url/.md como fonte final, para não responder
        // com atalho ou resumo no lugar da lei/portaria.
        ".pdf", ".html", ".htm", ".txt", ".docx", ".odt"
    };

    private static readonly HashSet<string> ImportSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Também aceitamos arquivos de apoio apenas para descobrir URLs oficiais
        // e baixar o documento real para .html/.pdf/.txt.
        ".pdf", ".html", ".htm", ".txt", ".docx", ".odt",
        ".url", ".csv", ".json", ".md"
    };

    private static readonly string[] OfficialDomains =
    [
        "planalto.gov.br",
        "sgex.eb.mil.br",
        "gov.br",
        "camara.leg.br",
        "senado.leg.br",
        "cpex.eb.mil.br",
        "dgp.eb.mil.br",
        "eb.mil.br"
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "que", "qual", "quais", "como", "para", "por", "com", "sem", "uma", "uns", "umas",
        "das", "dos", "deve", "pode", "podem", "tem", "sobre", "isso", "esta", "esse", "essa",
        "ser", "sao", "são", "não", "nao", "meu", "minha", "seu", "sua", "onde", "quando"
    };

    private static readonly Dictionary<string, string[]> QueryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["at"] = ["auxílio transporte", "auxilio transporte", "vale transporte"],
        ["auxilio transporte"] = ["auxílio transporte", "transporte", "cota parte"],
        ["auxílio transporte"] = ["auxilio transporte", "transporte", "cota parte"],
        ["fusex"] = ["fundo de saúde", "fundo de saude", "assistência médico hospitalar"],
        ["pnr"] = ["próprio nacional residencial", "proprio nacional residencial", "permissionário"],
        ["ir"] = ["imposto de renda", "irrf"],
        ["irrf"] = ["imposto de renda", "retenção na fonte"],
        ["grat rep"] = ["gratificação de representação", "gratificacao de representacao"],
        ["prec"] = ["prec-cp", "prec cp"],
        ["ferias"] = ["férias", "adicional de férias", "um terço de férias", "gozo de férias", "pagamento antecipadamente"],
        ["férias"] = ["ferias", "adicional de férias", "um terço de férias", "gozo de férias", "pagamento antecipadamente"],
        ["adicional de ferias"] = ["adicional de férias", "férias", "um terço", "remuneração militar"],
        ["adicional de férias"] = ["adicional de ferias", "férias", "um terço", "remuneração militar"],
        ["auxilio alimentacao"] = ["auxílio alimentação", "etapa comum", "alimentação", "férias"],
        ["auxílio alimentação"] = ["auxilio alimentacao", "etapa comum", "alimentação", "férias"],
        ["adicional natalino"] = ["décimo terceiro", "13º salário", "natalino", "antecipação"],
        ["exercicio anterior"] = ["exercício anterior", "dívida", "despesas de exercícios anteriores"],
        ["pensão"] = ["pensao", "pensão militar", "pensão judicial"],
        ["pensao"] = ["pensão", "pensão militar", "pensão judicial"],
        ["soldo"] = ["tabela de soldos", "remuneração militar"],
        ["pagamento"] = ["pagamento", "remuneração", "contracheque", "sippes", "cpex"]
    };

    private readonly AppPaths _paths;
    private readonly LogService _log;

    public LegislationService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(_paths.LegislationDocumentsDirectory);
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(_paths.LegislationDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.LegislationDatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 15
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public Task InitializeAsync()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS legislation_docs(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                file_name TEXT NOT NULL,
                path TEXT NOT NULL UNIQUE,
                size INTEGER NOT NULL,
                modified_ticks INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                page_count INTEGER NOT NULL DEFAULT 0,
                indexed_at TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS legislation_pages USING fts5(
                document_id UNINDEXED,
                title,
                file_name,
                page_no UNINDEXED,
                content,
                tokenize='unicode61 remove_diacritics 2'
            );
            """;
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public async Task<int> ImportFilesAsync(IEnumerable<string> files)
    {
        Directory.CreateDirectory(_paths.LegislationDocumentsDirectory);
        var count = 0;
        foreach (var source in files.Where(File.Exists))
            count += await ImportSourceFileAsync(source);

        await IndexAllAsync(force: false);
        return count;
    }

    public async Task<int> ImportFolderAsync(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        return await ImportFilesAsync(Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories));
    }

    public async Task<int> ImportZipAsync(string zipPath)
    {
        if (!File.Exists(zipPath)) return 0;
        var imported = 0;
        Directory.CreateDirectory(_paths.LegislationDocumentsDirectory);
        var tempRoot = Path.Combine(Path.GetTempPath(), "SIGFUR_legislacao_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue;
                var extension = Path.GetExtension(entry.Name);
                if (!ImportSourceExtensions.Contains(extension)) continue;
                var safeName = SafeFileName(entry.Name);
                var tempPath = UniquePath(Path.Combine(tempRoot, safeName));
                await using var source = entry.Open();
                await using (var target = File.Create(tempPath))
                    await source.CopyToAsync(target);
                imported += await ImportSourceFileAsync(tempPath, Path.GetFileNameWithoutExtension(entry.Name));
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }

        await IndexAllAsync(force: false);
        return imported;
    }

    private async Task<int> ImportSourceFileAsync(string source, string? preferredTitle = null)
    {
        var extension = Path.GetExtension(source);
        if (!ImportSourceExtensions.Contains(extension)) return 0;

        if (SupportedExtensions.Contains(extension))
        {
            var destination = UniquePath(Path.Combine(_paths.LegislationDocumentsDirectory, SafeFileName(Path.GetFileName(source))));
            File.Copy(source, destination, false);
            return 1;
        }

        // .url/.csv/.json/.md entram apenas como índice de links oficiais: o SIGFUR
        // baixa o conteúdo real e salva como .html/.pdf/.txt antes de indexar.
        var urls = await ExtractUrlsFromSourceAsync(source);
        var count = 0;
        foreach (var url in urls)
        {
            var title = string.IsNullOrWhiteSpace(preferredTitle) ? GuessTitleFromUrl(url) : preferredTitle;
            if (await TryDownloadOfficialDocumentAsync(url, title)) count++;
        }
        return count;
    }

    private static async Task<List<string>> ExtractUrlsFromSourceAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        var urls = Regex.Matches(text, "https?://[^\\s\\\"'<>]+", RegexOptions.IgnoreCase)
            .Select(match => match.Value.Trim().TrimEnd('.', ';', ',', ')', ']'))
            .Where(IsOfficialUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var direct = Regex.Match(text, @"^URL\s*=\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (direct.Success)
            {
                var url = direct.Groups[1].Value.Trim();
                if (IsOfficialUrl(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                    urls.Insert(0, url);
            }
        }
        return urls;
    }

    private async Task<bool> TryDownloadOfficialDocumentAsync(string url, string? preferredTitle = null)
    {
        if (!IsOfficialUrl(url)) return false;
        try
        {
            using var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            })
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SIGFUR/1.0 LegislationIndexer");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/pdf,text/plain,*/*;q=0.8");

            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) return false;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return false;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var finalUri = response.RequestMessage?.RequestUri?.ToString() ?? url;
            var extension = ResolveDownloadedExtension(finalUri, contentType, bytes);
            if (!SupportedExtensions.Contains(extension)) extension = ".html";

            var name = SafeFileName((string.IsNullOrWhiteSpace(preferredTitle) ? GuessTitleFromUrl(finalUri) : preferredTitle) + extension);
            var destination = UniquePath(Path.Combine(_paths.LegislationDocumentsDirectory, name));
            await File.WriteAllBytesAsync(destination, bytes);
            return true;
        }
        catch (Exception ex)
        {
            try { await _log.WriteAsync($"Falha ao baixar legislação: {url}", ex); } catch { }
            return false;
        }
    }

    private static string ResolveDownloadedExtension(string url, string contentType, byte[] bytes)
    {
        var pathExtension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (pathExtension is ".pdf" or ".html" or ".htm" or ".txt" or ".docx" or ".odt")
            return pathExtension;
        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46) return ".pdf";
        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase)) return ".pdf";
        if (contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)) return ".txt";
        if (contentType.Contains("word", StringComparison.OrdinalIgnoreCase) || contentType.Contains("officedocument", StringComparison.OrdinalIgnoreCase)) return ".docx";
        return ".html";
    }

    private static bool IsOfficialUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return OfficialDomains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    private static string GuessTitleFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(name)) name = uri.Host.Replace('.', '_');
            if (!string.IsNullOrWhiteSpace(uri.Query))
            {
                var cod = Regex.Match(uri.Query, @"codarquivo=([^&]+)", RegexOptions.IgnoreCase);
                if (cod.Success) name += "_cod_" + cod.Groups[1].Value;
            }
            return Regex.Replace(System.Net.WebUtility.UrlDecode(name).Replace('_', ' '), @"\s+", " ").Trim();
        }
        catch
        {
            return "documento oficial";
        }
    }

    public async Task<LegislationStats> IndexAllAsync(bool force, IProgress<string>? progress = null)
    {
        await InitializeAsync();
        var files = Directory.EnumerateFiles(_paths.LegislationDocumentsDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .ToList();

        using var connection = Open();
        await RemoveMissingDocumentsAsync(connection);

        foreach (var path in files)
        {
            try
            {
                var info = new FileInfo(path);
                var sha = await Sha256Async(path);
                long id = 0;
                var needsIndexing = true;

                using (var check = connection.CreateCommand())
                {
                    check.CommandText = "SELECT id,size,modified_ticks,sha256 FROM legislation_docs WHERE path=$path";
                    check.Parameters.AddWithValue("$path", path);
                    using var reader = await check.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        id = reader.GetInt64(0);
                        needsIndexing = force
                            || reader.GetInt64(1) != info.Length
                            || reader.GetInt64(2) != info.LastWriteTimeUtc.Ticks
                            || !reader.GetString(3).Equals(sha, StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (!needsIndexing) continue;
                progress?.Report("Indexando " + info.Name + "…");
                var pages = await ExtractPagesAsync(path);
                using var transaction = connection.BeginTransaction();

                if (id == 0)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        INSERT INTO legislation_docs(title,file_name,path,size,modified_ticks,sha256,page_count,indexed_at)
                        VALUES($title,$file,$path,$size,$ticks,$sha,$pages,$indexed);
                        SELECT last_insert_rowid();
                        """;
                    BindDocument(insert, path, info, sha, pages.Count);
                    id = Convert.ToInt64(await insert.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
                }
                else
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = """
                        UPDATE legislation_docs
                           SET title=$title,file_name=$file,size=$size,modified_ticks=$ticks,sha256=$sha,page_count=$pages,indexed_at=$indexed
                         WHERE id=$id;
                        """;
                    BindDocument(update, path, info, sha, pages.Count);
                    update.Parameters.AddWithValue("$id", id);
                    await update.ExecuteNonQueryAsync();

                    using var deletePages = connection.CreateCommand();
                    deletePages.Transaction = transaction;
                    deletePages.CommandText = "DELETE FROM legislation_pages WHERE document_id=$id";
                    deletePages.Parameters.AddWithValue("$id", id);
                    await deletePages.ExecuteNonQueryAsync();
                }

                for (var index = 0; index < pages.Count; index++)
                {
                    using var insertPage = connection.CreateCommand();
                    insertPage.Transaction = transaction;
                    insertPage.CommandText = """
                        INSERT INTO legislation_pages(document_id,title,file_name,page_no,content)
                        VALUES($id,$title,$file,$page,$content)
                        """;
                    insertPage.Parameters.AddWithValue("$id", id);
                    insertPage.Parameters.AddWithValue("$title", GuessTitle(path));
                    insertPage.Parameters.AddWithValue("$file", info.Name);
                    insertPage.Parameters.AddWithValue("$page", index + 1);
                    insertPage.Parameters.AddWithValue("$content", pages[index]);
                    await insertPage.ExecuteNonQueryAsync();
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                await _log.WriteAsync("Falha ao indexar legislação: " + path, ex);
            }
        }
        return await GetStatsAsync();
    }

    public async Task<List<LegislationSearchHit>> SearchAsync(string query, int limit = 120)
    {
        await InitializeAsync();
        var terms = ImportantTerms(query);
        if (terms.Count == 0) return [];

        var fts = string.Join(" OR ", terms.Select(term => $"\"{term.Replace("\"", string.Empty)}\""));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.document_id,p.title,p.file_name,d.path,p.page_no,p.content,bm25(legislation_pages) AS score
              FROM legislation_pages AS p
              JOIN legislation_docs AS d ON d.id=p.document_id
             WHERE legislation_pages MATCH $query
             ORDER BY score
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", fts);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var list = new List<LegislationSearchHit>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var content = reader.GetString(5);
            list.Add(new LegislationSearchHit
            {
                DocumentId = reader.GetInt64(0),
                Title = reader.GetString(1),
                FileName = reader.GetString(2),
                Path = reader.GetString(3),
                Page = Convert.ToInt32(reader.GetInt64(4), CultureInfo.InvariantCulture),
                Score = -reader.GetDouble(6),
                Snippet = Snippet(content, terms)
            });
        }
        return list;
    }

    public async Task<string> GetPageTextAsync(long documentId, int page)
    {
        await InitializeAsync();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM legislation_pages WHERE document_id=$id AND page_no=$page LIMIT 1";
        command.Parameters.AddWithValue("$id", documentId);
        command.Parameters.AddWithValue("$page", page);
        return (await command.ExecuteScalarAsync())?.ToString() ?? string.Empty;
    }

    public async Task<string> GetDocumentTextAsync(long documentId, int maxCharacters = 120_000)
    {
        await InitializeAsync();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT page_no,content FROM legislation_pages WHERE document_id=$id ORDER BY CAST(page_no AS INTEGER)";
        command.Parameters.AddWithValue("$id", documentId);
        var builder = new StringBuilder();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            builder.AppendLine($"\n--- Página {reader.GetValue(0)} ---");
            builder.AppendLine(reader.GetString(1));
            if (builder.Length >= maxCharacters) break;
        }
        return builder.Length <= maxCharacters ? builder.ToString() : builder.ToString(0, maxCharacters) + "\n…";
    }

    public async Task<List<LegislationDocument>> ListDocumentsAsync()
    {
        await InitializeAsync();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,file_name,path,size,modified_ticks,page_count,indexed_at FROM legislation_docs ORDER BY title";
        var list = new List<LegislationDocument>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            DateTime.TryParse(reader.GetString(7), out var indexed);
            list.Add(new LegislationDocument
            {
                Id = reader.GetInt64(0),
                Title = reader.GetString(1),
                FileName = reader.GetString(2),
                Path = reader.GetString(3),
                Size = reader.GetInt64(4),
                ModifiedTicks = reader.GetInt64(5),
                PageCount = Convert.ToInt32(reader.GetInt64(6), CultureInfo.InvariantCulture),
                IndexedAt = indexed
            });
        }
        return list;
    }

    public async Task<LegislationStats> GetStatsAsync()
    {
        await InitializeAsync();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(page_count),0),MAX(indexed_at) FROM legislation_docs";
        using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        DateTime? indexed = null;
        if (!reader.IsDBNull(2) && DateTime.TryParse(reader.GetString(2), out var parsed)) indexed = parsed;
        return new LegislationStats
        {
            Documents = Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture),
            Pages = Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
            LastIndexed = indexed
        };
    }

    public async Task DeleteDocumentAsync(LegislationDocument document, bool deleteFile)
    {
        await InitializeAsync();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var pages = connection.CreateCommand())
        {
            pages.Transaction = transaction;
            pages.CommandText = "DELETE FROM legislation_pages WHERE document_id=$id";
            pages.Parameters.AddWithValue("$id", document.Id);
            await pages.ExecuteNonQueryAsync();
        }
        using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = "DELETE FROM legislation_docs WHERE id=$id";
            record.Parameters.AddWithValue("$id", document.Id);
            await record.ExecuteNonQueryAsync();
        }
        transaction.Commit();
        if (deleteFile && File.Exists(document.Path)) File.Delete(document.Path);
    }

    public async Task<string> AnswerAsync(string question, IReadOnlyList<LegislationSearchHit>? current = null)
    {
        var hits = current is { Count: > 0 } ? current.Take(16).ToList() : await SearchAsync(question, 16);
        if (hits.Count == 0)
            return "Não encontrei base suficiente nos documentos offline. Importe ou indexe a norma correspondente e refaça a pergunta.";

        var terms = ImportantTerms(question);
        var candidates = new List<(double Score, string Text, LegislationSearchHit Hit)>();
        foreach (var hit in hits)
        {
            var text = await GetPageTextAsync(hit.DocumentId, hit.Page);
            foreach (var sentence in Sentences(text))
            {
                var normalizedSentence = Normalize(sentence);
                var score = terms.Sum(term => normalizedSentence.Contains(Normalize(term), StringComparison.OrdinalIgnoreCase)
                    ? 3 + Math.Min(12, term.Length) / 12d
                    : 0);
                if (Regex.IsMatch(sentence, @"\b(art\.?|artigo|portaria|lei|decreto|percentual|deverá|será|direito|vedado|prazo|inciso|parágrafo)\b", RegexOptions.IgnoreCase)) score += 1;
                if (score > 0) candidates.Add((score, sentence, hit));
            }
        }

        var chosen = candidates
            .OrderByDescending(x => x.Score)
            .GroupBy(x => Normalize(x.Text))
            .Select(group => group.First())
            .Take(6)
            .ToList();
        if (chosen.Count == 0)
            return "Localizei documentos relacionados, mas nenhum trecho foi forte o bastante para montar uma resposta segura. Abra os resultados e confira as páginas indicadas.";

        var builder = new StringBuilder();
        builder.AppendLine("RESPOSTA BASEADA SOMENTE NA BIBLIOTECA OFFLINE").AppendLine();
        for (var index = 0; index < chosen.Count; index++)
            builder.AppendLine($"{index + 1}. {chosen[index].Text.Trim()} [{chosen[index].Hit.Title}, p. {chosen[index].Hit.Page}]");
        builder.AppendLine().AppendLine("FONTES PARA CONFERÊNCIA");
        foreach (var source in chosen.Select(x => x.Hit.Reference).Distinct(StringComparer.OrdinalIgnoreCase))
            builder.AppendLine("• " + source);
        builder.AppendLine().AppendLine("Conferência recomendada: abra as páginas citadas antes de adotar a informação em documento oficial ou pagamento.");
        return builder.ToString();
    }

    private static void BindDocument(SqliteCommand command, string path, FileInfo info, string sha, int pageCount)
    {
        command.Parameters.AddWithValue("$title", GuessTitle(path));
        command.Parameters.AddWithValue("$file", info.Name);
        if (command.CommandText.Contains("$path", StringComparison.Ordinal)) command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$size", info.Length);
        command.Parameters.AddWithValue("$ticks", info.LastWriteTimeUtc.Ticks);
        command.Parameters.AddWithValue("$sha", sha);
        command.Parameters.AddWithValue("$pages", pageCount);
        command.Parameters.AddWithValue("$indexed", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
    }

    private static async Task<List<string>> ExtractPagesAsync(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".pdf")
        {
            return await Task.Run(() =>
            {
                var pages = new List<string>();
                using var pdf = PdfDocument.Open(path);
                foreach (var page in pdf.GetPages()) pages.Add(Clean(page.Text));
                return pages;
            });
        }
        if (extension is ".html" or ".htm") return [Clean(ExtractHtml(await File.ReadAllTextAsync(path)))];
        if (extension == ".txt") return [Clean(await File.ReadAllTextAsync(path))];
        if (extension == ".docx") return [Clean(ExtractDocx(path))];
        if (extension == ".odt") return [Clean(ExtractOdt(path))];
        return [];
    }

    private static string ExtractDocx(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        return document.MainDocumentPart?.Document?.InnerText ?? string.Empty;
    }


    private static string ExtractHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var text = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(p|div|li|tr|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return text;
    }

    private static string ExtractOdt(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("content.xml");
        if (entry is null) return string.Empty;
        using var reader = new StreamReader(entry.Open());
        var document = XDocument.Parse(reader.ReadToEnd());
        return string.Join(" ", document.DescendantNodes().OfType<XText>().Select(node => node.Value));
    }

    private async Task RemoveMissingDocumentsAsync(SqliteConnection connection)
    {
        var missing = new List<long>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT id,path FROM legislation_docs";
            using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var storedPath = reader.GetString(1);
                if (!File.Exists(storedPath) || !SupportedExtensions.Contains(Path.GetExtension(storedPath)))
                    missing.Add(reader.GetInt64(0));
            }
        }
        foreach (var id in missing)
        {
            using var transaction = connection.BeginTransaction();
            using var pages = connection.CreateCommand();
            pages.Transaction = transaction;
            pages.CommandText = "DELETE FROM legislation_pages WHERE document_id=$id";
            pages.Parameters.AddWithValue("$id", id);
            await pages.ExecuteNonQueryAsync();
            using var document = connection.CreateCommand();
            document.Transaction = transaction;
            document.CommandText = "DELETE FROM legislation_docs WHERE id=$id";
            document.Parameters.AddWithValue("$id", id);
            await document.ExecuteNonQueryAsync();
            transaction.Commit();
        }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static List<string> ImportantTerms(string query)
    {
        var normalized = Normalize(query);
        var terms = Regex.Matches(normalized, @"[a-z0-9ºª]{2,}")
            .Select(match => match.Value)
            .Where(term => term.Length > 2 && !StopWords.Contains(term))
            .ToList();

        foreach (var alias in QueryAliases)
        {
            if (!normalized.Contains(Normalize(alias.Key), StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var expansion in alias.Value)
                terms.AddRange(Regex.Matches(Normalize(expansion), @"[a-z0-9ºª]{3,}").Select(match => match.Value));
        }
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
    }

    private static string GuessTitle(string path)
        => Regex.Replace(Path.GetFileNameWithoutExtension(path).Replace('_', ' '), @"\s+", " ").Trim();

    private static string Clean(string value)
        => Regex.Replace((value ?? string.Empty).Replace('\0', ' '), @"\s+", " ").Trim();

    private static string Normalize(string value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(decomposed
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static string Snippet(string content, IReadOnlyList<string> terms)
    {
        var normalized = Normalize(content);
        var index = terms.Select(term => normalized.IndexOf(Normalize(term), StringComparison.OrdinalIgnoreCase))
            .Where(value => value >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, index - 180);
        var length = Math.Min(content.Length - start, 600);
        return (start > 0 ? "… " : string.Empty) + content.Substring(start, length) + (start + length < content.Length ? " …" : string.Empty);
    }

    private static IEnumerable<string> Sentences(string text)
        => Regex.Split(Clean(text), @"(?<=[\.!?;])\s+").Where(sentence => sentence.Length is >= 35 and <= 1_200);

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(Path.GetFileName(name).Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "documento" : safe;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem}_{index}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{stem}_{DateTime.Now:yyyyMMddHHmmss}{extension}");
    }
}
