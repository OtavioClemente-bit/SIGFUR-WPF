using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class OrganizationCatalogEntry
{
    public string Name { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string OfficialUrl { get; set; } = string.Empty;
    public string InstagramUrl { get; set; } = string.Empty;
    public List<string> MediaUrls { get; set; } = [];
    public string CachedLogoPath { get; set; } = string.Empty;
    public string CachedLogoSourceUrl { get; set; } = string.Empty;
    public string CachedLogoSourceName { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string LocationText => string.Join(" — ", new[] { City, State }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string DisplayName => string.IsNullOrWhiteSpace(LocationText) ? Name : $"{Name} — {LocationText}";
    public string SearchText => $"{Name} {City} {State} {Address} {District} {ZipCode} {Email} {InstagramUrl}";
    public bool HasCachedImage => !string.IsNullOrWhiteSpace(CachedLogoPath) && File.Exists(CachedLogoPath);
    public string ImageStatusText => HasCachedImage ? "✓ Imagem salva" : "Pendente";
    public string ImageSourceText => HasCachedImage
        ? (string.IsNullOrWhiteSpace(CachedLogoSourceName) ? "Cache local" : CachedLogoSourceName)
        : "Sem imagem";
}

public sealed class OrganizationImageCandidate
{
    public string Title { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string SourcePageUrl { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Score { get; set; }

    public string PreviewUrl => string.IsNullOrWhiteSpace(ThumbnailUrl) ? ImageUrl : ThumbnailUrl;
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "Resolução não informada";
    public bool IsInstagram => SourcePageUrl.Contains("instagram.com", StringComparison.OrdinalIgnoreCase)
                               || ImageUrl.Contains("instagram.com", StringComparison.OrdinalIgnoreCase);
    public string SourceDisplay => IsInstagram ? "Instagram" : SourceName;
}

public sealed record OrganizationImageResolution(string LocalPath, string SourceName, string SourceUrl);

public sealed class OrganizationCatalogCache
{
    public string SourceUrl { get; set; } = OrganizationCatalogService.OfficialDirectoryUrl;
    public DateTime UpdatedAt { get; set; }
    public List<OrganizationCatalogEntry> Items { get; set; } = [];
}
