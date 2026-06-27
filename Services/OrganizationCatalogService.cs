using System.Net;
using System.Net.Http.Headers;
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
        return cache?.Items?.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList() ?? [];
    }

    public async Task<IReadOnlyList<OrganizationCatalogEntry>> RefreshFromOfficialDirectoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(OfficialDirectoryUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var items = ParseDirectory(html);
            if (items.Count == 0)
                throw new InvalidOperationException("O portal oficial respondeu, mas nenhuma OM pôde ser identificada.");

            var previous = (await LoadCachedAsync()).ToDictionary(x => BuildKey(x.Name, x.City, x.State), StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (previous.TryGetValue(BuildKey(item.Name, item.City, item.State), out var old))
                    item.CachedLogoPath = old.CachedLogoPath;
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

        var candidateSites = new List<string>();
        if (IsPublicWebUrl(entry.OfficialUrl)) candidateSites.Add(entry.OfficialUrl);
        candidateSites.AddRange(entry.MediaUrls.Where(IsPublicWebUrl));

        foreach (var site in candidateSites.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var imageUrl = await FindInstitutionalImageAsync(site, cancellationToken);
                if (string.IsNullOrWhiteSpace(imageUrl)) continue;
                var saved = await DownloadImageAsync(imageUrl, entry.Name, cancellationToken);
                if (string.IsNullOrWhiteSpace(saved)) continue;
                entry.CachedLogoPath = saved;
                await UpdateCachedEntryAsync(entry);
                return saved;
            }
            catch (Exception ex)
            {
                await _log.WriteAsync($"Não foi possível obter automaticamente a imagem da OM {entry.Name} em {site}.", ex);
            }
        }
        return string.Empty;
    }

    private async Task UpdateCachedEntryAsync(OrganizationCatalogEntry changed)
    {
        var cache = await _json.LoadAsync<OrganizationCatalogCache>(_paths.OrganizationCatalogCacheFile) ?? new OrganizationCatalogCache();
        var key = BuildKey(changed.Name, changed.City, changed.State);
        var current = cache.Items.FirstOrDefault(x => BuildKey(x.Name, x.City, x.State).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (current is null) cache.Items.Add(changed);
        else current.CachedLogoPath = changed.CachedLogoPath;
        cache.UpdatedAt = DateTime.Now;
        await _json.SaveAsync(_paths.OrganizationCatalogCacheFile, cache);
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
