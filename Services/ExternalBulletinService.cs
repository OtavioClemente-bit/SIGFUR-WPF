using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Biblioteca permanente dos boletins externos da 4ª RM e do CML.
/// No Boletim Regional a 1ª Parte (Serviços Diários) é descartada antes da pesquisa.
/// No Aditamento CML o leitor destaca o bloco de autorização/liberação de pagamento.
/// </summary>
public sealed class ExternalBulletinService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly PdfTextService _pdfText;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> _pageMemoryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Uri CmlOnlineRoot = new("https://intranet.cml.eb.mil.br/");
    private const string CmlOnlinePage = "https://intranet.cml.eb.mil.br/index.php/pt/boletins-aditamentos/aditamentos/interno-2";
    private static readonly HttpClient CmlOnlineHttp = CreateCmlOnlineHttpClient();
    private static readonly Uri RegionOnlineRoot = new("https://intranet.4rm.eb.mil.br/");
    private const string RegionOnlinePage = "https://intranet.4rm.eb.mil.br/index.php/boletins/bol-rg-4arm/145-boletim-regional";
    private static readonly HttpClient RegionOnlineHttp = CreateRegionOnlineHttpClient();

    private static readonly Regex RegionalNumberRegex = new(@"BOLETIM\s+REGIONAL\s+N[º°O]?\s*([0-9]{1,4}/[0-9]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CmlHeaderRegex = new(@"ADITAMENTO\s+(?<secao>.+?)\s+N[º°O]?\s*(?<adt>[0-9]{1,4}/[0-9]{4})\s+BI\s+N[º°O]?\s*(?<bi>[0-9]{1,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex NumericDateRegex = new(@"\b(?<d>\d{1,2})[/-](?<m>\d{1,2})[/-](?<y>20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex LongDateRegex = new(@"\b(?<d>\d{1,2})\s+de\s+(?<m>[A-Za-zÀ-ÿ]+)\s+de\s+(?<y>20\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmountRegex = new(@"R\$\s*[0-9.]+(?:,[0-9]{2})?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EventRegex = new(@"Descri[cç][aã]o\s+do\s+Evento\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DurationRegex = new(@"Dura[cç][aã]o\s+em\s+dias\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PersonnelRegex = new(@"Efetivo\s+autorizado\s+por\s+posto\s+e\s+gradua[cç][aã]o\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CmlOnlineRowRegex = new(
        "<tr[^>]*>\\s*<td[^>]*>.*?<a[^>]*href\\s*=\\s*['\"](?<href>[^'\"]+\\.pdf)['\"][^>]*>.*?</a>.*?</td>\\s*<td[^>]*>(?<size>.*?)</td>\\s*<td[^>]*>(?<published>.*?)</td>\\s*</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RegionYearLinkRegex = new(
        "href\\s*=\\s*['\"](?<href>[^'\"]*/145-boletim-regional/(?<code>\\d+)-(?<year>\\d{4}))['\"]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RegionMonthLinkRegex = new(
        "<a[^>]*href\\s*=\\s*['\"](?<href>[^'\"]*/145-boletim-regional/\\d+-(?<year>\\d{4})/\\d+-(?<month>0[1-9]|1[0-2])-(?<slug>[^'\"/?#]+))['\"][^>]*>(?<label>.*?)</a>\\s*(?:<small>\\s*\\((?<count>\\d+)\\)\\s*</small>)?",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RegionDownloadLinkRegex = new(
        "<a[^>]*href\\s*=\\s*['\"](?<href>[^'\"]*[?&](?:amp;)?download=[^'\"]+)['\"][^>]*>(?<label>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public ExternalBulletinService(AppPaths paths, JsonFileService json, PdfTextService pdfText, LogService log)
    {
        _paths = paths;
        _json = json;
        _pdfText = pdfText;
        _log = log;
        Directory.CreateDirectory(_paths.ExternalBulletinDirectory);
        Directory.CreateDirectory(_paths.ExternalBulletinRegionDirectory);
        Directory.CreateDirectory(_paths.ExternalBulletinCmlDirectory);
    }

    public async Task<ExternalBulletinStore> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await _json.LoadAsync<ExternalBulletinStore>(_paths.ExternalBulletinIndexFile) ?? new ExternalBulletinStore();
            store.Items ??= [];
            foreach (var item in store.Items)
            {
                item.Mentions ??= [];
                item.StoredPath = ResolvePath(item.StoredPath);
                foreach (var mention in item.Mentions)
                {
                    mention.PdfPath = item.StoredPath;
                    mention.FileId = item.Id;
                    mention.Kind = item.Kind;
                    mention.Bulletin = item.DisplayNumber;
                    mention.BulletinDate = item.DisplayDate;
                }
            }
            return store;
        }
        finally { _gate.Release(); }
    }

    public async Task<ExternalBulletinSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
        => await _json.LoadAsync<ExternalBulletinSettings>(_paths.ExternalBulletinSettingsFile) ?? new ExternalBulletinSettings();

    public Task SaveSettingsAsync(ExternalBulletinSettings settings, CancellationToken cancellationToken = default)
        => _json.SaveAsync(_paths.ExternalBulletinSettingsFile, settings);

    public async Task<List<ExternalBulletinOnlineFile>> SearchCmlOnlineAsync(
        int year,
        string searchTerm,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        searchTerm = CleanSearchTerm(searchTerm);
        if (year is < 2010 or > 2100) throw new InvalidOperationException("Selecione um ano válido entre 2010 e 2100.");
        if (string.IsNullOrWhiteSpace(searchTerm)) throw new InvalidOperationException("Informe o texto que deve ser pesquisado na intranet do CML.");

        progress?.Report($"Consultando os aditamentos de {year} na intranet do CML...");
        try
        {
            var requestUri = BuildCmlOnlineSearchUri(year, searchTerm);
            EnsureOfficialCmlUri(requestUri, requirePdf: false);
            using var response = await CmlOnlineHttp.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = ParseCmlOnlineRows(html, year);

            var store = await LoadAsync(cancellationToken);
            var existing = store.Items
                .Where(x => x.Kind == ExternalBulletinKinds.Cml && File.Exists(x.StoredPath))
                .Select(x => x.OriginalFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in parsed)
            {
                item.AlreadyInLibrary = existing.Contains(item.FileName);
                item.IsSelected = !item.AlreadyInLibrary;
                item.Status = item.AlreadyInLibrary ? "Já está na biblioteca" : "Disponível para baixar";
            }
            progress?.Report(parsed.Count == 0
                ? $"Nenhum PDF encontrado para “{searchTerm}” em {year}."
                : $"{parsed.Count} PDF(s) encontrado(s) para “{searchTerm}” em {year}.");
            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await _log.WriteAsync("Falha ao consultar os aditamentos online do CML.", ex);
            throw new InvalidOperationException("Não foi possível acessar a intranet do CML. Confirme a conexão com a rede/VPN do Exército e tente novamente.", ex);
        }
    }

    public async Task<ExternalBulletinOnlineDownloadResult> DownloadAndImportCmlOnlineAsync(
        IEnumerable<ExternalBulletinOnlineFile> selection,
        string searchTerm,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        searchTerm = CleanSearchTerm(searchTerm);
        var selected = selection.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Marque ao menos um aditamento para baixar.");

        var result = new ExternalBulletinOnlineDownloadResult();
        var staging = Path.Combine(Path.GetTempPath(), "SIGFUR", "CmlBulletins", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var downloaded = new List<(ExternalBulletinOnlineFile Item, string Path)>();
        try
        {
            for (var index = 0; index < selected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = selected[index];
                if (item.AlreadyInLibrary)
                {
                    item.Status = "Já estava salvo";
                    result.AlreadyStored++;
                    continue;
                }

                progress?.Report($"Baixando {index + 1}/{selected.Count}: {item.FileName}");
                item.Status = "Baixando...";
                try
                {
                    var uri = new Uri(item.Url, UriKind.Absolute);
                    EnsureOfficialCmlUri(uri, requirePdf: true);
                    using var response = await CmlOnlineHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F' || bytes[4] != '-')
                        throw new InvalidDataException("O arquivo recebido não é um PDF válido.");
                    if (bytes.LongLength > 150L * 1024 * 1024)
                        throw new InvalidDataException("O PDF ultrapassa o limite de segurança de 150 MB.");

                    var path = Path.Combine(staging, SafeFileName(item.FileName));
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                    downloaded.Add((item, path));
                    item.Status = "Baixado · aguardando indexação";
                }
                catch (Exception ex)
                {
                    item.Status = "Falha no download";
                    result.Errors.Add($"{item.FileName}: {ex.Message}");
                    await _log.WriteAsync($"Falha ao baixar aditamento do CML: {item.Url}", ex);
                }
            }

            if (downloaded.Count > 0)
            {
                progress?.Report("Salvando e indexando os PDFs na biblioteca do SIGFUR...");
                result.Import = await ImportAsync(ExternalBulletinKinds.Cml, downloaded.Select(x => x.Path), searchTerm, progress, cancellationToken);
                result.Downloaded = downloaded.Count;
                result.Errors.AddRange(result.Import.Errors);
                await AttachOnlineSourcesAsync(downloaded.Select(x => x.Item), ExternalBulletinKinds.Cml, CmlOnlinePage, cancellationToken);
                foreach (var pair in downloaded)
                {
                    pair.Item.AlreadyInLibrary = true;
                    pair.Item.IsSelected = false;
                    pair.Item.Status = "Salvo e indexado";
                }
            }
            return result;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    public async Task<List<ExternalBulletinRegionPeriod>> GetRegionOnlinePeriodsAsync(
        int year,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (year is < 2000 or > 2100) throw new InvalidOperationException("Selecione um ano válido entre 2000 e 2100.");
        progress?.Report($"Localizando {year} na intranet da 4ª Região Militar...");
        try
        {
            var rootHtml = await GetRegionOnlineHtmlAsync(new Uri(RegionOnlinePage), cancellationToken);
            var years = ParseRegionOnlineYears(rootHtml);
            if (!years.TryGetValue(year, out var yearUri))
                throw new InvalidOperationException($"O ano {year} não está publicado na página de Boletins Regionais da 4ª RM.");

            progress?.Report($"Carregando os meses disponíveis de {year}...");
            var yearHtml = await GetRegionOnlineHtmlAsync(yearUri, cancellationToken);
            var periods = ParseRegionOnlinePeriods(yearHtml, year)
                .OrderByDescending(x => x.Month)
                .ToList();
            if (periods.Count == 0)
                throw new InvalidOperationException($"Nenhum mês foi localizado para {year} na página da 4ª RM.");
            return periods;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await _log.WriteAsync("Falha ao consultar os períodos online da 4ª RM.", ex);
            throw new InvalidOperationException("Não foi possível acessar a intranet da 4ª RM. Confirme a conexão com a rede/VPN do Exército e tente novamente.", ex);
        }
    }

    public async Task<List<ExternalBulletinOnlineFile>> SearchRegionOnlineAsync(
        int year,
        int month,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (month is < 0 or > 12) throw new InvalidOperationException("Selecione um mês válido ou a opção Todos os meses.");
        try
        {
            var periods = await GetRegionOnlinePeriodsAsync(year, progress, cancellationToken);
            var selectedPeriods = month == 0 ? periods : periods.Where(x => x.Month == month).ToList();
            if (selectedPeriods.Count == 0)
                throw new InvalidOperationException($"O mês selecionado não está publicado para {year}.");

            var result = new List<ExternalBulletinOnlineFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < selectedPeriods.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var period = selectedPeriods[index];
                progress?.Report($"Lendo {period.DisplayName} · {index + 1}/{selectedPeriods.Count}...");
                var html = await GetRegionOnlineHtmlAsync(new Uri(period.Url), cancellationToken);
                foreach (var item in ParseRegionOnlineFiles(html, year, period))
                    if (seen.Add(item.Url)) result.Add(item);
            }

            var store = await LoadAsync(cancellationToken);
            var existing = store.Items
                .Where(x => x.Kind == ExternalBulletinKinds.Region && File.Exists(x.StoredPath))
                .Select(x => x.OriginalFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in result)
            {
                item.AlreadyInLibrary = existing.Contains(item.FileName);
                item.IsSelected = !item.AlreadyInLibrary;
                item.Status = item.AlreadyInLibrary ? "Já está na biblioteca" : "Disponível para baixar";
            }

            progress?.Report(result.Count == 0
                ? $"Nenhum PDF localizado para o período selecionado de {year}."
                : $"{result.Count} PDF(s) localizado(s) na 4ª RM.");
            return result.OrderByDescending(x => x.PublishedAt).ThenBy(x => x.FileName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await _log.WriteAsync("Falha ao consultar os Boletins Regionais online da 4ª RM.", ex);
            throw new InvalidOperationException("Não foi possível acessar a intranet da 4ª RM. Confirme a conexão com a rede/VPN do Exército e tente novamente.", ex);
        }
    }

    public async Task<ExternalBulletinOnlineDownloadResult> DownloadAndImportRegionOnlineAsync(
        IEnumerable<ExternalBulletinOnlineFile> selection,
        string searchTerm,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        searchTerm = CleanSearchTerm(searchTerm);
        var selected = selection.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Marque ao menos um Boletim Regional para baixar.");

        var result = new ExternalBulletinOnlineDownloadResult();
        var staging = Path.Combine(Path.GetTempPath(), "SIGFUR", "RegionBulletins", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var downloaded = new List<(ExternalBulletinOnlineFile Item, string Path)>();
        try
        {
            for (var index = 0; index < selected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = selected[index];
                if (item.AlreadyInLibrary)
                {
                    item.Status = "Já estava salvo";
                    result.AlreadyStored++;
                    continue;
                }

                progress?.Report($"Baixando {index + 1}/{selected.Count}: {item.FileName}");
                item.Status = "Baixando...";
                try
                {
                    var uri = new Uri(item.Url, UriKind.Absolute);
                    EnsureOfficialRegionUri(uri, requireDownload: true);
                    using var response = await RegionOnlineHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    EnsurePdfBytes(bytes);

                    var path = Path.Combine(staging, SafeFileName(item.FileName));
                    await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                    downloaded.Add((item, path));
                    item.Status = "Baixado · aguardando indexação";
                }
                catch (Exception ex)
                {
                    item.Status = "Falha no download";
                    result.Errors.Add($"{item.FileName}: {ex.Message}");
                    await _log.WriteAsync($"Falha ao baixar Boletim Regional da 4ª RM: {item.Url}", ex);
                }
            }

            if (downloaded.Count > 0)
            {
                progress?.Report("Salvando e indexando os Boletins Regionais na biblioteca do SIGFUR...");
                result.Import = await ImportAsync(ExternalBulletinKinds.Region, downloaded.Select(x => x.Path), searchTerm, progress, cancellationToken);
                result.Downloaded = downloaded.Count;
                result.Errors.AddRange(result.Import.Errors);
                await AttachOnlineSourcesAsync(downloaded.Select(x => x.Item), ExternalBulletinKinds.Region, RegionOnlinePage, cancellationToken);
                foreach (var pair in downloaded)
                {
                    pair.Item.AlreadyInLibrary = true;
                    pair.Item.IsSelected = false;
                    pair.Item.Status = "Salvo e indexado";
                }
            }
            return result;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    public async Task<ExternalBulletinImportResult> ImportAsync(
        string kind,
        IEnumerable<string> sources,
        string searchTerm,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        searchTerm = CleanSearchTerm(searchTerm);
        if (string.IsNullOrWhiteSpace(searchTerm)) throw new InvalidOperationException("Informe o nome ou a OM que deve ser pesquisada.");

        var result = new ExternalBulletinImportResult();
        var pdfs = ExpandPdfSources(sources).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (pdfs.Count == 0) return result;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            for (var index = 0; index < pdfs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = pdfs[index];
                progress?.Report($"Lendo {index + 1}/{pdfs.Count}: {Path.GetFileName(source)}");
                try
                {
                    var hash = await HashFileAsync(source, cancellationToken);
                    var sameHash = store.Items.FirstOrDefault(x => x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && x.HashSha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
                    if (sameHash is not null)
                    {
                        if (!Normalize(sameHash.IndexedSearchTerm).Equals(Normalize(searchTerm), StringComparison.Ordinal))
                        {
                            var pagesForTerm = await ExtractPagesCachedAsync(sameHash.StoredPath, sameHash.HashSha256, cancellationToken);
                            ApplyParse(sameHash, pagesForTerm, searchTerm);
                            result.Updated++;
                        }
                        else result.Duplicates++;
                        continue;
                    }

                    var pages = await ExtractPagesCachedAsync(source, hash, cancellationToken);
                    var parsed = ParseFile(kind, source, pages, searchTerm, hash);
                    parsed.SourceFolder = Path.GetDirectoryName(Path.GetFullPath(source)) ?? string.Empty;
                    parsed.OriginalFileName = Path.GetFileName(source);
                    parsed.ImportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", PtBr);
                    parsed.SizeBytes = new FileInfo(source).Length;

                    var existing = store.Items.FirstOrDefault(x =>
                        x.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) &&
                        x.DisplayNumber != "—" &&
                        Normalize(x.DisplayNumber).Equals(Normalize(parsed.DisplayNumber), StringComparison.Ordinal) &&
                        Normalize(x.DisplayDate).Equals(Normalize(parsed.DisplayDate), StringComparison.Ordinal));

                    var destination = existing is null ? CreateDestination(parsed) : existing.StoredPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                        File.Copy(source, destination, true);
                    parsed.StoredPath = destination;
                    BindMentionPaths(parsed);

                    if (existing is null)
                    {
                        store.Items.Add(parsed);
                        result.Imported++;
                    }
                    else
                    {
                        parsed.Id = existing.Id;
                        parsed.IsRead = existing.IsRead;
                        parsed.ReadAt = existing.ReadAt;
                        BindMentionPaths(parsed);
                        store.Items[store.Items.IndexOf(existing)] = parsed;
                        result.Updated++;
                    }
                    if (parsed.MentionCount == 0) result.WithoutMention++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
                    await _log.WriteAsync($"Falha ao importar boletim externo: {source}", ex);
                }
            }

            SortStore(store);
            await WriteStoreUnsafeAsync(store, cancellationToken);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task ReindexAllAsync(string searchTerm, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        => await ReindexKindsAsync([ExternalBulletinKinds.Region, ExternalBulletinKinds.Cml], searchTerm, progress, cancellationToken);

    public async Task ReindexAsync(string kind, string searchTerm, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateKind(kind);
        await ReindexKindsAsync([kind], searchTerm, progress, cancellationToken);
    }

    private async Task ReindexKindsAsync(
        IReadOnlyCollection<string> kinds,
        string searchTerm,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        searchTerm = CleanSearchTerm(searchTerm);
        if (string.IsNullOrWhiteSpace(searchTerm)) throw new InvalidOperationException("Informe o nome ou a OM que deve ser pesquisada.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            var candidates = store.Items
                .Where(x => kinds.Contains(x.Kind, StringComparer.OrdinalIgnoreCase) && File.Exists(x.StoredPath))
                .ToList();
            var extracted = new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            var completed = 0;
            await Parallel.ForEachAsync(candidates, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
                CancellationToken = cancellationToken
            }, async (item, token) =>
            {
                try
                {
                    extracted[item.Id] = await ExtractPagesCachedAsync(item.StoredPath, item.HashSha256, token);
                }
                catch (Exception ex)
                {
                    await _log.WriteAsync($"Falha ao reindexar boletim externo: {item.StoredPath}", ex);
                }
                finally
                {
                    var current = Interlocked.Increment(ref completed);
                    progress?.Report($"Preparando pesquisa {current}/{candidates.Count}: {item.OriginalFileName}");
                }
            });

            foreach (var item in candidates)
                if (extracted.TryGetValue(item.Id, out var pages))
                    ApplyParse(item, pages, searchTerm);

            SortStore(store);
            await WriteStoreUnsafeAsync(store, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SetReadStateAsync(string id, bool isRead, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            var item = store.Items.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (item is null || item.Kind != ExternalBulletinKinds.Region) return;
            item.IsRead = isRead;
            item.ReadAt = isRead ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", PtBr) : string.Empty;
            await WriteStoreUnsafeAsync(store, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string id, bool deleteStoredPdf, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            var item = store.Items.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (item is null) return;
            store.Items.Remove(item);
            await WriteStoreUnsafeAsync(store, cancellationToken);
            if (deleteStoredPdf)
            {
                try { if (File.Exists(item.StoredPath)) File.Delete(item.StoredPath); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    public void OpenPdf(ExternalBulletinMention mention)
    {
        if (mention is null) return;
        OpenPdf(mention.PdfPath, mention.PdfSearchTerm, mention.DocumentOccurrence);
    }

    public void OpenPdf(ExternalBulletinFile file, string searchTerm = "")
        => OpenPdf(file.StoredPath, searchTerm, 1);

    public void OpenPdf(string path, string searchTerm, int occurrence)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("O PDF do boletim não foi encontrado.", path);

        ShellService.OpenPath(path);
        var term = CleanSearchTerm(searchTerm);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(term)) return;
        try { Clipboard.SetText(term); } catch { }

        _ = Task.Run(async () =>
        {
            await Task.Delay(1850);
            SendCtrlFAndPaste();
            await Task.Delay(650);
            PressKey(VkEscape);
            await Task.Delay(120);
            for (var index = 1; index < Math.Max(1, occurrence); index++)
            {
                PressKey(VkF3);
                await Task.Delay(105);
            }
        });
    }

    private async Task<IReadOnlyList<string>> ExtractPagesCachedAsync(string path, string hash, CancellationToken cancellationToken)
    {
        var key = !string.IsNullOrWhiteSpace(hash) ? hash : Path.GetFullPath(path);
        var lazy = _pageMemoryCache.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<string>>>(
            () => _pdfText.ExtractPagesAsync(path, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch
        {
            _pageMemoryCache.TryRemove(key, out _);
            throw;
        }
    }

    private ExternalBulletinFile ParseFile(string kind, string source, IReadOnlyList<string> pages, string searchTerm, string hash)
    {
        var item = new ExternalBulletinFile
        {
            Id = hash[..Math.Min(20, hash.Length)],
            HashSha256 = hash,
            Kind = kind,
            BulletinType = ExternalBulletinKinds.DisplayName(kind),
            OriginalFileName = Path.GetFileName(source),
            Pages = pages.Count
        };
        ApplyMetadata(item, pages, source);
        ApplyParse(item, pages, searchTerm);
        return item;
    }

    private void ApplyParse(ExternalBulletinFile item, IReadOnlyList<string> pages, string searchTerm)
    {
        item.Pages = pages.Count;
        item.IndexedSearchTerm = searchTerm;
        item.Mentions = ParseMentions(item, pages, searchTerm, out var ignored);
        item.IgnoredFirstPartMentions = ignored;
        BindMentionPaths(item);
    }

    private List<ExternalBulletinMention> ParseMentions(ExternalBulletinFile file, IReadOnlyList<string> pages, string searchTerm, out int ignoredFirstPart)
    {
        var result = new List<ExternalBulletinMention>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var target = Normalize(searchTerm);
        var preparedPages = pages
            .Select((page, index) => new
            {
                Page = index + 1,
                Lines = CleanPage(page).Split('\n').Select(OneLine).Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
            })
            .ToList();
        var startPageIndex = 0;
        var startLineIndex = 0;
        var documentOccurrence = 0;
        ignoredFirstPart = 0;

        if (file.Kind == ExternalBulletinKinds.Region)
        {
            startPageIndex = -1;
            var dailyServicesSeen = false;
            for (var pageIndex = 0; pageIndex < preparedPages.Count && startPageIndex < 0; pageIndex++)
            {
                for (var lineIndex = 0; lineIndex < preparedPages[pageIndex].Lines.Count; lineIndex++)
                {
                    var line = preparedPages[pageIndex].Lines[lineIndex];
                    if (IsDailyServicesStartMarker(line)) dailyServicesSeen = true;
                    if (!dailyServicesSeen || !IsAfterDailyServicesMarker(line)) continue;
                    startPageIndex = pageIndex;
                    startLineIndex = lineIndex + 1;
                    break;
                }
            }
            if (startPageIndex < 0)
            {
                // Aditamentos regionais não possuem a estrutura 1ª/2ª Parte.
                // Neles todo o documento é útil; no Boletim Regional principal,
                // a ausência de um corte confirmado bloqueia a pesquisa por segurança.
                if (!IsRegionalAddendum(file)) return result;
                startPageIndex = 0;
                startLineIndex = 0;
            }

            // Mantém a ocorrência real do termo no PDF para o Ctrl+F abrir na menção
            // certa, mas não classifica nem monta contexto da seção Serviços Diários.
            for (var pageIndex = 0; pageIndex <= startPageIndex; pageIndex++)
            {
                var limit = pageIndex == startPageIndex ? startLineIndex : preparedPages[pageIndex].Lines.Count;
                for (var lineIndex = 0; lineIndex < limit; lineIndex++)
                    ignoredFirstPart += CountOccurrences(Normalize(preparedPages[pageIndex].Lines[lineIndex]), target);
            }
            documentOccurrence = ignoredFirstPart;
        }

        for (var pageIndex = startPageIndex; pageIndex < preparedPages.Count; pageIndex++)
        {
            var lines = preparedPages[pageIndex].Lines;
            var firstLine = pageIndex == startPageIndex ? startLineIndex : 0;
            for (var lineIndex = firstLine; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                var occurrencesInLine = CountOccurrences(Normalize(line), target);
                if (occurrencesInLine == 0) continue;

                for (var occurrenceInLine = 0; occurrenceInLine < occurrencesInLine; occurrenceInLine++)
                {
                    documentOccurrence++;
                    var mention = BuildMention(file, lines, lineIndex, preparedPages[pageIndex].Page, searchTerm, documentOccurrence, firstLine);
                    var key = $"{mention.Page}|{mention.DocumentOccurrence}|{Normalize(mention.MatchLine)}";
                    if (seen.Add(key)) result.Add(mention);
                }
            }
        }
        return result;
    }

    private ExternalBulletinMention BuildMention(
        ExternalBulletinFile file,
        IReadOnlyList<string> lines,
        int lineIndex,
        int page,
        string searchTerm,
        int occurrence,
        int searchableFrom = 0)
    {
        var isCml = file.Kind == ExternalBulletinKinds.Cml;
        var before = isCml ? 22 : 9;
        var after = isCml ? 5 : 8;
        var contextStart = Math.Max(searchableFrom, lineIndex - before);
        var contextLines = lines.Skip(contextStart).Take(Math.Min(lines.Count, lineIndex + after + 1) - contextStart).ToList();
        var context = string.Join("\n", contextLines);
        var line = lines[lineIndex];
        var section = FindSection(lines, lineIndex, isCml ? 70 : 45, searchableFrom);

        var mention = new ExternalBulletinMention
        {
            Id = $"{file.Id}:{page}:{occurrence}",
            FileId = file.Id,
            Kind = file.Kind,
            Bulletin = file.DisplayNumber,
            BulletinDate = file.DisplayDate,
            Page = page,
            DocumentOccurrence = occurrence,
            PdfSearchTerm = searchTerm,
            Section = section,
            MatchLine = line,
            Context = context,
            PdfPath = file.StoredPath
        };

        if (isCml) PopulateCmlDetails(mention, context);
        else PopulateRegionDetails(mention, lines, lineIndex, searchTerm);
        mention.Id = StableMentionId(file, mention);
        return mention;
    }

    private static void PopulateRegionDetails(ExternalBulletinMention mention, IReadOnlyList<string> lines, int index, string searchTerm)
    {
        var normalizedLine = Normalize(lines[index]);
        var target = Normalize(searchTerm);
        if (normalizedLine == target)
        {
            mention.Type = "Bloco da OM";
            var members = lines.Skip(index + 1).Take(4).Where(x => !LooksLikeHeading(x)).ToList();
            mention.Summary = members.Count == 0 ? lines[index] : $"{lines[index]} · {string.Join(" · ", members)}";
        }
        else if (normalizedLine.StartsWith("- o cmt", StringComparison.Ordinal) || normalizedLine.Contains("providencias decorrentes", StringComparison.Ordinal))
        {
            mention.Type = "Providência / consequência";
            mention.Summary = lines[index];
        }
        else if (Regex.IsMatch(lines[index], @"\b(?:S\s*Ten|[1-3][º°]\s*Sgt|[1-2][º°]\s*Ten|Cap|Maj|Ten\s*Cel|Cel|Cb|Sd)\b", RegexOptions.IgnoreCase))
        {
            mention.Type = "Militar / ato publicado";
            mention.Summary = lines[index];
        }
        else
        {
            mention.Type = "Menção administrativa";
            mention.Summary = lines[index];
        }
    }

    private static void PopulateCmlDetails(ExternalBulletinMention mention, string context)
    {
        var normalized = Normalize(context);
        mention.Type = normalized.Contains("exercicios anteriores", StringComparison.Ordinal)
            ? "Liberação para pagamento - Exercícios anteriores"
            : normalized.Contains("autorizo o saque", StringComparison.Ordinal)
                ? "Autorização de saque"
                : "Menção em aditamento do CML";

        mention.Amount = AmountRegex.Matches(context).Cast<Match>().Select(x => x.Value.Trim()).LastOrDefault() ?? string.Empty;
        mention.Event = MatchLine(EventRegex, context);
        mention.Duration = MatchLine(DurationRegex, context);
        mention.Personnel = MatchLine(PersonnelRegex, context);

        var parts = new List<string> { mention.Type };
        if (!string.IsNullOrWhiteSpace(mention.Event)) parts.Add(mention.Event);
        if (!string.IsNullOrWhiteSpace(mention.Amount)) parts.Add(mention.Amount);
        mention.Summary = string.Join(" · ", parts);
    }

    private static string MatchLine(Regex regex, string context)
    {
        var match = regex.Match(context);
        return match.Success ? OneLine(match.Groups[1].Value) : string.Empty;
    }

    private static string FindSection(IReadOnlyList<string> lines, int index, int lookBehind, int searchableFrom = 0)
    {
        var headings = new List<string>();
        for (var i = index; i >= Math.Max(searchableFrom, index - lookBehind); i--)
        {
            var line = lines[i];
            if (IsPageNoise(line)) continue;
            var normalized = Normalize(line);
            if (Regex.IsMatch(normalized, @"^[234]a\s+parte\b") || LooksLikeHeading(line))
            {
                var clean = OneLine(line);
                if (!headings.Any(x => Normalize(x) == Normalize(clean))) headings.Add(clean);
                if (headings.Count >= 3) break;
            }
        }
        headings.Reverse();
        return string.Join(" › ", headings);
    }

    private static bool LooksLikeHeading(string value)
    {
        var line = OneLine(value);
        if (line.Length is < 4 or > 125 || IsPageNoise(line)) return false;
        var letters = line.Where(char.IsLetter).ToArray();
        if (letters.Length < 4) return false;
        var upper = letters.Count(char.IsUpper);
        return upper >= Math.Max(4, (int)Math.Ceiling(letters.Length * 0.72)) ||
               Regex.IsMatch(line, @"^[a-z0-9]+[.)]\s+[A-ZÁÉÍÓÚÂÊÔÃÕÇ]", RegexOptions.IgnoreCase);
    }

    private static bool IsPageNoise(string value)
    {
        var normalized = Normalize(value);
        return normalized.StartsWith("continuacao do ", StringComparison.Ordinal) ||
               normalized.StartsWith("pag n", StringComparison.Ordinal) ||
               normalized.Contains("original assinado", StringComparison.Ordinal);
    }

    private static bool IsDailyServicesStartMarker(string line)
    {
        var normalized = Normalize(line);
        return Regex.IsMatch(normalized, @"^(?:1a|primeira)\s+parte\b") ||
               normalized is "servicos diarios";
    }

    private static bool IsRegionalAddendum(ExternalBulletinFile file)
    {
        var name = Normalize(file.OriginalFileName);
        return name.StartsWith("adt ", StringComparison.Ordinal) ||
               name.StartsWith("aditamento ", StringComparison.Ordinal);
    }

    private static bool IsAfterDailyServicesMarker(string line)
    {
        var normalized = Normalize(line);
        return Regex.IsMatch(normalized, @"^(?:2a|segunda)\s+parte\b");
    }

    private static int CountOccurrences(string text, string target)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(target)) return 0;
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(target, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += Math.Max(1, target.Length);
        }
        return count;
    }

    private static string StableMentionId(ExternalBulletinFile file, ExternalBulletinMention mention)
    {
        var raw = $"{file.Kind}|{file.HashSha256}|{mention.Page}|{mention.DocumentOccurrence}|{Normalize(mention.MatchLine)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }

    private void ApplyMetadata(ExternalBulletinFile item, IReadOnlyList<string> pages, string source)
    {
        var fullText = string.Join("\n", pages.Take(4));
        if (item.Kind == ExternalBulletinKinds.Region)
        {
            item.BulletinType = "Boletim Regional";
            var match = RegionalNumberRegex.Match(fullText);
            item.BulletinNumber = match.Success ? match.Groups[1].Value.Trim() : InferNumberFromFileName(source, "Bol Rg");
        }
        else
        {
            item.BulletinType = "Aditamento CML";
            var match = CmlHeaderRegex.Match(fullText);
            if (match.Success)
            {
                var section = OneLine(match.Groups["secao"].Value);
                item.BulletinNumber = $"Adt {section} Nr {match.Groups["adt"].Value} · BI {match.Groups["bi"].Value}";
            }
            else item.BulletinNumber = InferNumberFromFileName(source, "Adt CML");
        }

        var date = ParseDate(fullText);
        item.BulletinDate = date?.ToString("dd/MM/yyyy", PtBr) ?? "—";
        item.DateIso = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static DateTime? ParseDate(string text)
    {
        var longDate = LongDateRegex.Match(text);
        if (longDate.Success && TryMonth(longDate.Groups["m"].Value, out var month) &&
            int.TryParse(longDate.Groups["d"].Value, out var day) && int.TryParse(longDate.Groups["y"].Value, out var year))
        {
            try { return new DateTime(year, month, day); } catch { }
        }
        var numeric = NumericDateRegex.Match(text);
        if (numeric.Success && int.TryParse(numeric.Groups["d"].Value, out var d) &&
            int.TryParse(numeric.Groups["m"].Value, out var m) && int.TryParse(numeric.Groups["y"].Value, out var y))
        {
            try { return new DateTime(y, m, d); } catch { }
        }
        return null;
    }

    private static bool TryMonth(string value, out int month)
    {
        month = Normalize(value) switch
        {
            "janeiro" or "jan" => 1,
            "fevereiro" or "fev" => 2,
            "marco" or "mar" => 3,
            "abril" or "abr" => 4,
            "maio" or "mai" => 5,
            "junho" or "jun" => 6,
            "julho" or "jul" => 7,
            "agosto" or "ago" => 8,
            "setembro" or "set" => 9,
            "outubro" or "out" => 10,
            "novembro" or "nov" => 11,
            "dezembro" or "dez" => 12,
            _ => 0
        };
        return month > 0;
    }

    private string CreateDestination(ExternalBulletinFile item)
    {
        var root = item.Kind == ExternalBulletinKinds.Cml ? _paths.ExternalBulletinCmlDirectory : _paths.ExternalBulletinRegionDirectory;
        var date = DateTime.TryParseExact(item.DateIso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : (DateTime?)null;
        var folder = date is null ? Path.Combine(root, "Sem_Data") : Path.Combine(root, date.Value.ToString("yyyy"), date.Value.ToString("MM"));
        Directory.CreateDirectory(folder);
        var clean = SafeFileName(Path.GetFileNameWithoutExtension(item.OriginalFileName));
        var suffix = item.HashSha256.Length >= 8 ? item.HashSha256[..8] : Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(folder, $"{clean}_{suffix}.pdf");
    }

    private static string InferNumberFromFileName(string path, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var number = Regex.Match(name, @"\b(?:Nr\s*)?0*([0-9]{1,4})(?:[/_-](20\d{2}))?\b", RegexOptions.IgnoreCase);
        if (!number.Success) return prefix;
        return number.Groups[2].Success ? $"{number.Groups[1].Value}/{number.Groups[2].Value}" : number.Groups[1].Value;
    }

    private static IEnumerable<string> ExpandPdfSources(IEnumerable<string> sources)
    {
        foreach (var source in sources.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (File.Exists(source) && Path.GetExtension(source).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(source);
                continue;
            }
            if (!Directory.Exists(source)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(source, "*.pdf", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files) yield return Path.GetFullPath(file);
        }
    }

    private async Task<ExternalBulletinStore> ReadStoreUnsafeAsync(CancellationToken cancellationToken)
    {
        var store = await _json.LoadAsync<ExternalBulletinStore>(_paths.ExternalBulletinIndexFile) ?? new ExternalBulletinStore();
        store.Items ??= [];
        foreach (var item in store.Items)
        {
            item.Mentions ??= [];
            item.StoredPath = ResolvePath(item.StoredPath);
            BindMentionPaths(item);
        }
        return store;
    }

    private async Task WriteStoreUnsafeAsync(ExternalBulletinStore store, CancellationToken cancellationToken)
    {
        foreach (var item in store.Items)
        {
            item.StoredPath = MakeRelative(item.StoredPath);
            foreach (var mention in item.Mentions) mention.PdfPath = item.StoredPath;
        }
        await _json.SaveAsync(_paths.ExternalBulletinIndexFile, store);
        foreach (var item in store.Items)
        {
            item.StoredPath = ResolvePath(item.StoredPath);
            BindMentionPaths(item);
        }
    }

    private void BindMentionPaths(ExternalBulletinFile item)
    {
        foreach (var mention in item.Mentions)
        {
            mention.FileId = item.Id;
            mention.Kind = item.Kind;
            mention.Bulletin = item.DisplayNumber;
            mention.BulletinDate = item.DisplayDate;
            mention.PdfPath = item.StoredPath;
        }
    }

    private string MakeRelative(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(_paths.DataDirectory) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(_paths.DataDirectory, full) : full;
        }
        catch { return path; }
    }

    private string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(_paths.DataDirectory, path)); }
        catch { return path; }
    }

    private async Task AttachOnlineSourcesAsync(
        IEnumerable<ExternalBulletinOnlineFile> onlineFiles,
        string kind,
        string sourceFolder,
        CancellationToken cancellationToken)
    {
        var sources = onlineFiles
            .GroupBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Url, StringComparer.OrdinalIgnoreCase);
        if (sources.Count == 0) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreUnsafeAsync(cancellationToken);
            foreach (var item in store.Items.Where(x => x.Kind == kind))
            {
                if (!sources.TryGetValue(item.OriginalFileName, out var sourceUrl)) continue;
                item.SourceUrl = sourceUrl;
                item.SourceFolder = sourceFolder;
            }
            await WriteStoreUnsafeAsync(store, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static HttpClient CreateCmlOnlineHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SIGFUR/ExternalBulletinDownloader");
        client.DefaultRequestHeaders.Referrer = new Uri(CmlOnlinePage);
        return client;
    }

    private static HttpClient CreateRegionOnlineHttpClient()
    {
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 SIGFUR/RegionalBulletinDownloader");
        client.DefaultRequestHeaders.Referrer = new Uri(RegionOnlinePage);
        return client;
    }

    private static async Task<string> GetRegionOnlineHtmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        EnsureOfficialRegionUri(uri, requireDownload: false);
        using var response = await RegionOnlineHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static Dictionary<int, Uri> ParseRegionOnlineYears(string html)
    {
        var result = new Dictionary<int, Uri>();
        foreach (Match match in RegionYearLinkRegex.Matches(html ?? string.Empty))
        {
            if (!int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)) continue;
            var rawHref = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (!Uri.TryCreate(RegionOnlineRoot, rawHref, out var uri)) continue;
            try { EnsureOfficialRegionUri(uri, requireDownload: false); } catch { continue; }
            result.TryAdd(year, uri);
        }
        return result;
    }

    private static List<ExternalBulletinRegionPeriod> ParseRegionOnlinePeriods(string html, int year)
    {
        var result = new List<ExternalBulletinRegionPeriod>();
        var seen = new HashSet<int>();
        foreach (Match match in RegionMonthLinkRegex.Matches(html ?? string.Empty))
        {
            if (!int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var linkYear) || linkYear != year) continue;
            if (!int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var month) || !seen.Add(month)) continue;
            var rawHref = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (!Uri.TryCreate(RegionOnlineRoot, rawHref, out var uri)) continue;
            try { EnsureOfficialRegionUri(uri, requireDownload: false); } catch { continue; }

            var label = StripOnlineHtml(match.Groups["label"].Value);
            var name = Regex.Replace(label, @"^\s*\d{2}\s*-\s*", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = match.Groups["slug"].Value.Replace('-', ' ').Trim();
            name = name.ToUpper(PtBr);
            _ = int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count);
            result.Add(new ExternalBulletinRegionPeriod
            {
                Year = year,
                Month = month,
                Name = name,
                Url = uri.AbsoluteUri,
                DocumentCount = count
            });
        }
        return result;
    }

    private static List<ExternalBulletinOnlineFile> ParseRegionOnlineFiles(
        string html,
        int year,
        ExternalBulletinRegionPeriod period)
    {
        var result = new List<ExternalBulletinOnlineFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in RegionDownloadLinkRegex.Matches(html ?? string.Empty))
        {
            var label = StripOnlineHtml(match.Groups["label"].Value);
            if (string.IsNullOrWhiteSpace(label) || label.Equals("Download", StringComparison.OrdinalIgnoreCase)) continue;
            var rawHref = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (!Uri.TryCreate(RegionOnlineRoot, rawHref, out var uri) || !seen.Add(uri.AbsoluteUri)) continue;
            try { EnsureOfficialRegionUri(uri, requireDownload: true); } catch { continue; }
            if (!label.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) label += ".pdf";
            result.Add(new ExternalBulletinOnlineFile
            {
                Year = year,
                FileName = SafeFileName(label),
                Url = uri.AbsoluteUri,
                SizeKb = "—",
                PublishedAt = $"{period.Month:00}/{year}"
            });
        }
        return result;
    }

    private static void EnsureOfficialRegionUri(Uri uri, bool requireDownload)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(RegionOnlineRoot.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O endereço retornado não pertence à intranet oficial da 4ª Região Militar.");
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.StartsWith("/index.php/boletins/bol-rg-4arm/145-boletim-regional", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O endereço retornado não pertence à área oficial de Boletins Regionais.");
        if (requireDownload && !Regex.IsMatch(WebUtility.HtmlDecode(uri.Query), @"(?:^\?|&)download=\d+:", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("O link retornado não é um download oficial de Boletim Regional.");
    }

    private static void EnsurePdfBytes(byte[] bytes)
    {
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F' || bytes[4] != '-')
            throw new InvalidDataException("O arquivo recebido não é um PDF válido.");
        if (bytes.LongLength > 150L * 1024 * 1024)
            throw new InvalidDataException("O PDF ultrapassa o limite de segurança de 150 MB.");
    }

    private static Uri BuildCmlOnlineSearchUri(int year, string searchTerm)
    {
        var encodedTerm = Uri.EscapeDataString($"'{searchTerm}'");
        return new Uri(CmlOnlineRoot,
            $"99.php?id=99&ano={year.ToString(CultureInfo.InvariantCulture)}&base=images%2Fcmdocml%2F4sec%2Fadt&texto={encodedTerm}");
    }

    private static List<ExternalBulletinOnlineFile> ParseCmlOnlineRows(string html, int year)
    {
        var result = new List<ExternalBulletinOnlineFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in CmlOnlineRowRegex.Matches(html ?? string.Empty))
        {
            var rawHref = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (!Uri.TryCreate(CmlOnlineRoot, rawHref, out var uri)) continue;
            try { EnsureOfficialCmlUri(uri, requirePdf: true); } catch { continue; }
            var fileName = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
            if (string.IsNullOrWhiteSpace(fileName) || !seen.Add(uri.AbsoluteUri)) continue;
            result.Add(new ExternalBulletinOnlineFile
            {
                Year = year,
                FileName = fileName,
                Url = uri.AbsoluteUri,
                SizeKb = StripOnlineHtml(match.Groups["size"].Value),
                PublishedAt = StripOnlineHtml(match.Groups["published"].Value)
            });
        }
        return result;
    }

    private static string StripOnlineHtml(string value)
        => OneLine(WebUtility.HtmlDecode(Regex.Replace(value ?? string.Empty, "<[^>]+>", " ")));

    private static void EnsureOfficialCmlUri(Uri uri, bool requirePdf)
    {
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals(CmlOnlineRoot.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O endereço retornado não pertence à intranet oficial do CML.");
        if (!requirePdf) return;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.StartsWith("/images/cmdocml/4sec/adt/", StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O link retornado não é um aditamento PDF da 4ª Seção do CML.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void SortStore(ExternalBulletinStore store)
    {
        store.Items = store.Items
            .OrderBy(x => x.Kind == ExternalBulletinKinds.Region ? 0 : 1)
            .ThenByDescending(x => x.DateIso)
            .ThenByDescending(x => x.DisplayNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.OriginalFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string CleanPage(string value)
        => (value ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string OneLine(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string CleanSearchTerm(string? value)
        => OneLine(value).Trim('"', '\'', ' ');

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Replace('ª', 'a').Replace('º', 'o').Replace('°', 'o');
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(ch));
        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"[^a-z0-9]+", " ").Trim();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((value ?? "boletim").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '.', '_');
        return clean.Length > 100 ? clean[..100].Trim() : (string.IsNullOrWhiteSpace(clean) ? "boletim" : clean);
    }

    private static void ValidateKind(string kind)
    {
        if (kind != ExternalBulletinKinds.Region && kind != ExternalBulletinKinds.Cml)
            throw new ArgumentOutOfRangeException(nameof(kind), "Origem de boletim externo inválida.");
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    private const byte VkControl = 0x11, VkF = 0x46, VkV = 0x56, VkEscape = 0x1B, VkF3 = 0x72;
    private const uint KeyUp = 0x0002;

    private static void SendCtrlFAndPaste()
    {
        try
        {
            HotKey(VkF);
            Thread.Sleep(180);
            HotKey(VkV);
        }
        catch { }
    }

    private static void HotKey(byte key)
    {
        keybd_event(VkControl, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkControl, 0, KeyUp, UIntPtr.Zero);
    }

    private static void PressKey(byte key)
    {
        try
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        }
        catch { }
    }
}
