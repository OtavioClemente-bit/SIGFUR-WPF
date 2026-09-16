using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class OrganizationCatalogService
{
    public const string OfficialDirectoryUrl = "https://www.eb.mil.br/quarteis-por-estado";

    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;
    private readonly HttpClient _http;

    public OrganizationCatalogService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths;
        _json = json;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SIGFUR", "6.1.7"));
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en;q=0.6");
        Directory.CreateDirectory(_paths.OrganizationLogosDirectory);
    }

    public async Task<IReadOnlyList<OrganizationCatalogEntry>> LoadCachedAsync()
    {
        var cache = await _json.LoadAsync<OrganizationCatalogCache>(_paths.OrganizationCatalogCacheFile);
        return MergeCatalogs(cache?.Items ?? [], LoadBundledCodomCatalog());
    }

    public async Task<IReadOnlyList<OrganizationCatalogEntry>> RefreshFromOfficialDirectoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(OfficialDirectoryUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var items = MergeCatalogs(ParseDirectory(html), LoadBundledCodomCatalog()).ToList();
            if (items.Count == 0)
                throw new InvalidOperationException("O portal oficial respondeu, mas nenhuma OM pôde ser identificada.");

            var previous = (await LoadCachedAsync()).ToDictionary(x => BuildKey(x.Name, x.City, x.State), StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (previous.TryGetValue(BuildKey(item.Name, item.City, item.State), out var old))
                {
                    item.CachedLogoPath = old.CachedLogoPath;
                    item.CachedLogoSourceUrl = old.CachedLogoSourceUrl;
                    item.CachedLogoSourceName = old.CachedLogoSourceName;
                    item.InstagramUrl = old.InstagramUrl;
                }
            }

            var cache = new OrganizationCatalogCache
            {
                SourceUrl = OfficialDirectoryUrl,
                UpdatedAt = DateTime.Now,
                Items = items
            };
            await _json.SaveAsync(_paths.OrganizationCatalogCacheFile, cache);
            return items;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao atualizar o catálogo oficial de Organizações Militares.", ex);
            var cached = await LoadCachedAsync();
            if (cached.Count > 0) return cached;
            throw;
        }
    }

    public async Task<string> ResolveAndCacheLogoAsync(OrganizationCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(entry.CachedLogoPath) && File.Exists(entry.CachedLogoPath))
            return entry.CachedLogoPath;

        var candidates = await SearchImageCandidatesAsync(entry, cancellationToken);
        var selected = candidates.FirstOrDefault();
        if (selected is null) return string.Empty;
        var resolution = await CacheImageCandidateAsync(entry, selected, cancellationToken);
        return resolution?.LocalPath ?? string.Empty;
    }

    public async Task<IReadOnlyList<OrganizationImageCandidate>> SearchImageCandidatesAsync(
        OrganizationCatalogEntry entry,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<OrganizationImageCandidate>();

        var candidateSites = new List<string>();
        if (IsPublicWebUrl(entry.OfficialUrl)) candidateSites.Add(entry.OfficialUrl);
        candidateSites.AddRange((entry.MediaUrls ?? []).Where(IsPublicWebUrl));
        if (IsPublicWebUrl(entry.InstagramUrl)) candidateSites.Add(entry.InstagramUrl);
        candidateSites.AddRange(KnownInstitutionalPages(entry.Name));

        async Task<OrganizationImageCandidate?> SearchSiteAsync(string site)
        {
            try
            {
                var imageUrl = await FindInstitutionalImageAsync(site, cancellationToken);
                if (string.IsNullOrWhiteSpace(imageUrl)) return null;
                if (IsInstagramUrl(site) && imageUrl.Contains("static.cdninstagram.com/rsrc.php", StringComparison.OrdinalIgnoreCase))
                    return null;
                return new OrganizationImageCandidate
                {
                    Title = "Imagem institucional localizada na página da OM",
                    ImageUrl = imageUrl,
                    ThumbnailUrl = imageUrl,
                    SourcePageUrl = site,
                    SourceName = IsInstagramUrl(site) ? "Instagram" : IsFacebookUrl(site) ? "Facebook da OM" : IsArmyDomain(site) ? "Portal do Exército" : "Site institucional",
                    Score = IsArmyDomain(site) ? 240 : IsInstagramUrl(site) ? 220 : 180
                };
            }
            catch (Exception ex)
            {
                await _log.WriteAsync($"Não foi possível obter automaticamente a imagem da OM {entry.Name} em {site}.", ex);
                return null;
            }
        }

        var siteTasks = candidateSites.Distinct(StringComparer.OrdinalIgnoreCase).Select(SearchSiteAsync).ToList();
        if (siteTasks.Count > 0)
            candidates.AddRange((await Task.WhenAll(siteTasks)).OfType<OrganizationImageCandidate>());

        var queries = new[]
        {
            $"\"{entry.Name}\" brasão oficial Polícia do Exército",
            $"\"{entry.Name}\" distintivo emblema escudo",
            $"{entry.Name} logo Instagram Exército Brasileiro"
        };
        async Task<IReadOnlyList<OrganizationImageCandidate>> SearchQueryAsync(string query)
        {
            try
            {
                return await SearchPublicImagesAsync(query, entry, cancellationToken);
            }
            catch (Exception ex)
            {
                await _log.WriteAsync($"Falha na pesquisa pública de imagens para {entry.Name}.", ex);
                return [];
            }
        }

        var queryResults = await Task.WhenAll(queries.Select(SearchQueryAsync));
        foreach (var result in queryResults) candidates.AddRange(result);

        return candidates
            .Where(x => IsPublicWebUrl(x.ImageUrl) || IsPublicWebUrl(x.ThumbnailUrl))
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ImageUrl) ? x.ThumbnailUrl : x.ImageUrl, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.OrderByDescending(y => y.Score).First())
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Width * x.Height)
            .Take(18)
            .ToList()
            .AsReadOnly();
    }

    public async Task<OrganizationImageResolution?> CacheImageCandidateAsync(
        OrganizationCatalogEntry entry,
        OrganizationImageCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var path = await DownloadImageAsync(candidate.ImageUrl, entry.Name, cancellationToken);
        if (string.IsNullOrWhiteSpace(path) && !candidate.ThumbnailUrl.Equals(candidate.ImageUrl, StringComparison.OrdinalIgnoreCase))
            path = await DownloadImageAsync(candidate.ThumbnailUrl, entry.Name, cancellationToken);
        if (string.IsNullOrWhiteSpace(path)) return null;

        entry.CachedLogoPath = path;
        entry.CachedLogoSourceUrl = candidate.SourcePageUrl;
        entry.CachedLogoSourceName = string.IsNullOrWhiteSpace(candidate.SourceDisplay) ? "Pesquisa pública" : candidate.SourceDisplay;
        if (candidate.IsInstagram) entry.InstagramUrl = NormalizeInstagramProfileUrl(candidate.SourcePageUrl);
        await UpdateCachedEntryAsync(entry);
        return new OrganizationImageResolution(path, entry.CachedLogoSourceName, entry.CachedLogoSourceUrl);
    }

    public async Task ClearCachedImageAsync(OrganizationCatalogEntry entry)
    {
        entry.CachedLogoPath = string.Empty;
        entry.CachedLogoSourceUrl = string.Empty;
        entry.CachedLogoSourceName = string.Empty;
        entry.InstagramUrl = string.Empty;
        await UpdateCachedEntryAsync(entry);
    }

    private async Task UpdateCachedEntryAsync(OrganizationCatalogEntry changed)
    {
        var cache = await _json.LoadAsync<OrganizationCatalogCache>(_paths.OrganizationCatalogCacheFile) ?? new OrganizationCatalogCache();
        var key = BuildKey(changed.Name, changed.City, changed.State);
        var current = cache.Items.FirstOrDefault(x => BuildKey(x.Name, x.City, x.State).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (current is null) cache.Items.Add(changed);
        else
        {
            current.CachedLogoPath = changed.CachedLogoPath;
            current.CachedLogoSourceUrl = changed.CachedLogoSourceUrl;
            current.CachedLogoSourceName = changed.CachedLogoSourceName;
            current.InstagramUrl = changed.InstagramUrl;
        }
        cache.UpdatedAt = DateTime.Now;
        await _json.SaveAsync(_paths.OrganizationCatalogCacheFile, cache);
    }

    private async Task<IReadOnlyList<OrganizationImageCandidate>> SearchPublicImagesAsync(
        string query,
        OrganizationCatalogEntry entry,
        CancellationToken cancellationToken)
    {
        var encoded = Uri.EscapeDataString(query);
        var searchPageUrl = $"https://duckduckgo.com/?q={encoded}&iax=images&ia=images";
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, searchPageUrl);
        using var pageResponse = await _http.SendAsync(pageRequest, cancellationToken);
        if (!pageResponse.IsSuccessStatusCode) return [];
        var page = await pageResponse.Content.ReadAsStringAsync(cancellationToken);
        var token = Regex.Match(page, @"\bvqd\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.IgnoreCase).Groups["v"].Value;
        if (string.IsNullOrWhiteSpace(token)) return [];

        var endpoint = $"https://duckduckgo.com/i.js?l=br-pt&o=json&q={encoded}&vqd={Uri.EscapeDataString(token)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Referrer = new Uri("https://duckduckgo.com/");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];

        var found = new List<OrganizationImageCandidate>();
        foreach (var item in results.EnumerateArray().Take(35))
        {
            var image = JsonString(item, "image");
            var thumbnail = JsonString(item, "thumbnail");
            var title = JsonString(item, "title");
            var sourcePage = JsonString(item, "url");
            if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(thumbnail)) continue;
            var width = JsonInt(item, "width");
            var height = JsonInt(item, "height");
            var instagram = IsInstagramUrl(sourcePage) || IsInstagramUrl(image);
            var army = IsArmyDomain(sourcePage);
            var normalizedTitle = MilitaryRankService.Normalize(title);
            var matchesOrganization = TitleMatchesOrganization(title, entry.Name);
            if (!matchesOrganization) continue;
            var score = 40;
            if (instagram) score += 95;
            if (army) score += 110;
            if (Regex.IsMatch(normalizedTitle, @"\b(brasao|simbolo|emblema|escudo|logo|logotipo)\b", RegexOptions.IgnoreCase)) score += 80;
            if (matchesOrganization) score += 75;
            if (!string.IsNullOrWhiteSpace(entry.City) && normalizedTitle.Contains(MilitaryRankService.Normalize(entry.City), StringComparison.OrdinalIgnoreCase)) score += 18;
            if (width >= 300 && height >= 300) score += 15;
            if (width < 120 || height < 120) score -= 80;

            found.Add(new OrganizationImageCandidate
            {
                Title = string.IsNullOrWhiteSpace(title) ? entry.Name : title,
                ImageUrl = image,
                ThumbnailUrl = thumbnail,
                SourcePageUrl = sourcePage,
                SourceName = instagram ? "Instagram" : army ? "Portal do Exército" : "Pesquisa pública",
                Width = width,
                Height = height,
                Score = score
            });
        }
        return found;
    }

    private static string JsonString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int JsonInt(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static IReadOnlyList<string> KnownInstitutionalPages(string organization)
    {
        var normalized = MilitaryRankService.Normalize(organization);
        // Fontes públicas conhecidas são usadas como complemento quando o
        // diretório do EB não publica o site ou a rede social da unidade.
        return normalized switch
        {
            "4o cia pe" or "4a cia pe" =>
            [
                "https://www.facebook.com/4ciape",
                "https://www.instagram.com/4ciape/"
            ],
            _ => []
        };
    }

    private IReadOnlyList<OrganizationCatalogEntry> LoadBundledCodomCatalog()
    {
        try
        {
            if (!File.Exists(_paths.BulletinCodomCatalogFile)) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(_paths.BulletinCodomCatalogFile));
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
            var result = new List<OrganizationCatalogEntry>();
            foreach (var item in items.EnumerateArray())
            {
                var name = JsonString(item, "sigla").Trim();
                if (name.Length == 0) continue;
                var location = JsonString(item, "cidade_estado").Trim();
                var slash = location.LastIndexOf('/');
                result.Add(new OrganizationCatalogEntry
                {
                    Name = name,
                    City = slash > 0 ? SplitCamelWords(location[..slash]) : SplitCamelWords(location),
                    State = slash > 0 ? location[(slash + 1)..].Trim() : string.Empty,
                    UpdatedAt = DateTime.Now
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync("Falha ao carregar o catálogo CODOM integrado.", ex);
            return [];
        }
    }

    private static IReadOnlyList<OrganizationCatalogEntry> MergeCatalogs(
        IEnumerable<OrganizationCatalogEntry> primary,
        IEnumerable<OrganizationCatalogEntry> supplementary)
    {
        var merged = new Dictionary<string, OrganizationCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in supplementary.Concat(primary))
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            if (!merged.TryGetValue(entry.Name.Trim(), out var current)) merged[entry.Name.Trim()] = entry;
            else if (!string.IsNullOrWhiteSpace(entry.OfficialUrl) || !string.IsNullOrWhiteSpace(entry.CachedLogoPath)) merged[entry.Name.Trim()] = entry;
        }
        return merged.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string SplitCamelWords(string value)
        => Regex.Replace(value ?? string.Empty, @"(?<=[a-zà-ÿ])(?=[A-ZÀ-Þ])", " ").Trim();

    private static bool TitleMatchesOrganization(string title, string organization)
    {
        var normalizedTitle = MilitaryRankService.Normalize(title);
        var normalizedOrganization = MilitaryRankService.Normalize(organization);
        if (normalizedTitle.Contains(normalizedOrganization, StringComparison.OrdinalIgnoreCase)) return true;

        var titleWords = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var important = normalizedOrganization.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3 && x is not "companhia" and not "batalhao" and not "regimento" and not "exercito"
                        and not "organizacao" and not "militar" and not "comando" and not "centro")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (important.Count == 0) return false;
        var matched = important.Count(x => titleWords.Contains(x));
        var number = Regex.Match(normalizedOrganization, @"\b\d+[a-z]?\b").Value;
        var numberMatches = string.IsNullOrWhiteSpace(number) || Regex.IsMatch(normalizedTitle, $@"\b{Regex.Escape(number)}\b");
        return numberMatches && matched >= Math.Min(2, important.Count);
    }

    private static bool IsInstagramUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".instagram.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsFacebookUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Host.Equals("facebook.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsArmyDomain(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Host.Equals("eb.mil.br", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".eb.mil.br", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeInstagramProfileUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsInstagramUrl(value)) return value ?? string.Empty;
        var segment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (segment.Length == 0 || new[] { "p", "reel", "reels", "stories", "explore" }.Contains(segment, StringComparer.OrdinalIgnoreCase))
            return value ?? string.Empty;
        return $"https://www.instagram.com/{segment}/";
    }

    private async Task<string> FindInstitutionalImageAsync(string pageUrl, CancellationToken cancellationToken)
    {
        if (IsDirectImage(pageUrl)) return pageUrl;
        using var response = await _http.GetAsync(pageUrl, cancellationToken);
        if (!response.IsSuccessStatusCode) return string.Empty;
        var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (media.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return pageUrl;
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)) return string.Empty;

        var candidates = new List<(string Url, int Score)>();
        void Add(string? raw, int score)
        {
            var decoded = WebUtility.HtmlDecode(raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(decoded) || decoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            if (!Uri.TryCreate(baseUri, decoded, out var absolute) || !IsPublicWebUrl(absolute.ToString())) return;
            var url = absolute.ToString();
            if (url.Contains("spacer", StringComparison.OrdinalIgnoreCase) || url.Contains("pixel", StringComparison.OrdinalIgnoreCase)) return;
            candidates.Add((url, score));
        }

        foreach (Match tag in Regex.Matches(html, @"(?is)<meta\b[^>]*>"))
        {
            var content = ExtractHtmlAttribute(tag.Value, "content");
            var marker = string.Join(" ", ExtractHtmlAttribute(tag.Value, "property"), ExtractHtmlAttribute(tag.Value, "name"));
            if (marker.Contains("og:image", StringComparison.OrdinalIgnoreCase)) Add(content, 100);
            else if (marker.Contains("twitter:image", StringComparison.OrdinalIgnoreCase)) Add(content, 95);
        }

        foreach (Match tag in Regex.Matches(html, @"(?is)<link\b[^>]*>"))
        {
            var rel = ExtractHtmlAttribute(tag.Value, "rel");
            var href = ExtractHtmlAttribute(tag.Value, "href");
            if (rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase)) Add(href, 45);
            else if (rel.Contains("icon", StringComparison.OrdinalIgnoreCase)) Add(href, 25);
        }

        foreach (Match tag in Regex.Matches(html, @"(?is)<img\b[^>]*>"))
        {
            var descriptor = string.Join(" ",
                ExtractHtmlAttribute(tag.Value, "class"),
                ExtractHtmlAttribute(tag.Value, "id"),
                ExtractHtmlAttribute(tag.Value, "alt"),
                ExtractHtmlAttribute(tag.Value, "title"),
                ExtractHtmlAttribute(tag.Value, "src"));
            var normalized = MilitaryRankService.Normalize(descriptor);
            var score = 20;
            if (Regex.IsMatch(normalized, @"\b(brasao|simbolo|emblema|escudo)\b", RegexOptions.IgnoreCase)) score += 85;
            if (Regex.IsMatch(normalized, @"\b(logo|logotipo|brand|identidade)\b", RegexOptions.IgnoreCase)) score += 65;
            if (Regex.IsMatch(normalized, @"\b(exercito|organizacao militar|om)\b", RegexOptions.IgnoreCase)) score += 20;
            if (Regex.IsMatch(normalized, @"\b(banner|slide|carrossel|noticia|thumbnail|avatar)\b", RegexOptions.IgnoreCase)) score -= 25;
            Add(ExtractHtmlAttribute(tag.Value, "data-src"), score + 4);
            Add(ExtractHtmlAttribute(tag.Value, "data-lazy-src"), score + 3);
            Add(ExtractHtmlAttribute(tag.Value, "src"), score);
        }

        return candidates
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => IsDirectImage(x.Url))
            .Select(x => x.Url)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string ExtractHtmlAttribute(string tag, string attribute)
    {
        var match = Regex.Match(tag ?? string.Empty,
            $@"(?is)\b{Regex.Escape(attribute)}\s*=\s*(?:[""'](?<v>.*?)[""']|(?<v>[^\s>]+))");
        return match.Success ? match.Groups["v"].Value.Trim() : string.Empty;
    }

    private async Task<string> DownloadImageAsync(string url, string organizationName, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return string.Empty;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length < 256 || bytes.Length > 12_000_000 || !LooksLikeImage(bytes, mediaType)) return string.Empty;
        var ext = ExtensionFor(response.Content.Headers.ContentType?.MediaType, url);
        var file = Path.Combine(_paths.OrganizationLogosDirectory, SafeFileName(organizationName) + ext);
        await File.WriteAllBytesAsync(file, bytes, cancellationToken);
        return file;
    }

    private static bool LooksLikeImage(byte[] bytes, string mediaType)
    {
        if (mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase)) return false;
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
        if (bytes.Length < 12) return false;
        return (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
               || (bytes[0] == 0xFF && bytes[1] == 0xD8)
               || (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
               || (bytes[0] == 0x42 && bytes[1] == 0x4D)
               || (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50);
    }

    private static List<OrganizationCatalogEntry> ParseDirectory(string html)
    {
        var result = new List<OrganizationCatalogEntry>();
        var blocks = Regex.Matches(html, @"(?is)<h5[^>]*>(?<name>.*?)</h5>(?<body>.*?)(?=<h5\b|<h3\b|$)");
        foreach (Match block in blocks)
        {
            var name = CleanHtml(block.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var body = block.Groups["body"].Value;
            var plain = CleanHtml(body, preserveLines: true);
            var cityState = Capture(plain, @"Cidade\s*-\s*UF\s*:\s*(?<v>[^\r\n]+)");
            var city = cityState;
            var state = string.Empty;
            var cityMatch = Regex.Match(cityState, @"^(?<city>.+?)\s*-\s*(?<uf>[A-Z]{2})\s*$", RegexOptions.IgnoreCase);
            if (cityMatch.Success)
            {
                city = cityMatch.Groups["city"].Value.Trim();
                state = cityMatch.Groups["uf"].Value.ToUpperInvariant();
            }

            var links = Regex.Matches(body, @"(?is)href\s*=\s*[""'](?<u>https?://[^""']+)[""']")
                .Select(x => WebUtility.HtmlDecode(x.Groups["u"].Value.Trim()))
                .Where(IsPublicWebUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var official = links.FirstOrDefault(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Host.EndsWith(".eb.mil.br", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            var instagram = links.FirstOrDefault(IsInstagramUrl) ?? string.Empty;

            result.Add(new OrganizationCatalogEntry
            {
                Name = name,
                Address = Capture(plain, @"Endere[cç]o\s*:\s*(?<v>[^\r\n]+)"),
                District = Capture(plain, @"Bairro\s*:\s*(?<v>[^\r\n]+)"),
                City = city,
                State = state,
                ZipCode = Capture(plain, @"CEP\s*:\s*(?<v>[^\r\n]+)"),
                Phone = Capture(plain, @"Telefones?\s*:\s*(?<v>[^\r\n]+)"),
                Email = Capture(plain, @"E-mails?\s*:\s*(?<v>[^\r\n]+)"),
                OfficialUrl = official,
                InstagramUrl = NormalizeInstagramProfileUrl(instagram),
                MediaUrls = links,
                UpdatedAt = DateTime.Now
            });
        }
        return result
            .GroupBy(x => BuildKey(x.Name, x.City, x.State), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string Capture(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value.Trim().Trim('.', '…') : string.Empty;
    }

    private static string CleanHtml(string value, bool preserveLines = false)
    {
        var text = Regex.Replace(value ?? string.Empty, @"(?is)<(script|style|noscript).*?>.*?</\1>", " ");
        text = Regex.Replace(text, @"(?is)<br\s*/?>|</p>|</div>|</li>", "\n");
        text = Regex.Replace(text, @"(?is)<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');
        if (preserveLines)
        {
            var lines = text.Split('\n').Select(x => Regex.Replace(x, @"\s+", " ").Trim()).Where(x => x.Length > 0);
            return string.Join("\n", lines);
        }
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static bool IsPublicWebUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && !uri.Host.Contains("intranet", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectImage(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico" }.Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);

    private static string ExtensionFor(string? mediaType, string url)
    {
        var ext = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetExtension(uri.AbsolutePath) : string.Empty;
        if (new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico" }.Contains(ext, StringComparer.OrdinalIgnoreCase)) return ext.ToLowerInvariant();
        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/x-icon" => ".ico",
            _ => ".jpg"
        };
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((value ?? "OM").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        clean = Regex.Replace(clean, @"\s+", "_").Trim('_');
        return clean.Length > 110 ? clean[..110] : clean;
    }

    private static string BuildKey(string name, string city, string state) => $"{name}|{city}|{state}".Trim().ToUpperInvariant();
}
