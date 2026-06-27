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
    public List<string> MediaUrls { get; set; } = [];
    public string CachedLogoPath { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string LocationText => string.Join(" — ", new[] { City, State }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string DisplayName => string.IsNullOrWhiteSpace(LocationText) ? Name : $"{Name} — {LocationText}";
    public string SearchText => $"{Name} {City} {State} {Address} {District} {ZipCode} {Email}";
}

public sealed class OrganizationCatalogCache
{
    public string SourceUrl { get; set; } = OrganizationCatalogService.OfficialDirectoryUrl;
    public DateTime UpdatedAt { get; set; }
    public List<OrganizationCatalogEntry> Items { get; set; } = [];
}
