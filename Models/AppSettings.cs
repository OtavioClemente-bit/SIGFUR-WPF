namespace SIGFUR.Wpf.Models;

public sealed class UiProfile
{
    public string Rank { get; set; } = "3º Sgt";
    public string Operator { get; set; } = "Operador";
    public string Function { get; set; } = "Furriel";
    public string Organization { get; set; } = "Organização Militar";
    public string CommanderName { get; set; } = string.Empty;
    public string CommanderRank { get; set; } = string.Empty;
    public string LogoPath { get; set; } = string.Empty;
    public string LegacyProjectRoot { get; set; } = string.Empty;
    public List<string> OrganizationCatalog { get; set; } = OrganizationDefaults.FeaturedNames.ToList();
    public Dictionary<string, string> OrganizationImages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class OrganizationDefaults
{
    public static IReadOnlyList<string> FeaturedNames { get; } =
    [
        "2º BPE", "3º BPE", "4º BPE", "6º BPE", "7º BPE", "8° BPE", "9º BPE", "11º BPE",
        "4ª Cia PE", "15ª Cia PE", "16ª Cia PE"
    ];
}

public sealed class WindowStateData
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 820;
    public bool Maximized { get; set; }
    public bool NavigationCollapsed { get; set; }
    public double UiScale { get; set; } = 1.0;
}
