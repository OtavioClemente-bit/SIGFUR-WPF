using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Identidade institucional ativa durante a sessão. Centraliza a OM usada em
/// cabeçalhos e documentos para que os módulos não mantenham nomes fixos.
/// </summary>
public static class OrganizationIdentity
{
    private const string Fallback = "Organização Militar";
    private static string _name = Fallback;

    public static string Name => _name;
    public static string UpperName => _name.ToUpper(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    public static void Apply(UiProfile? profile) => Apply(profile?.Organization);

    public static void Apply(string? organization)
        => _name = string.IsNullOrWhiteSpace(organization) ? Fallback : organization.Trim();

    public static string BulletinIssuer => $"da {Name}";
    public static string CommandRole => $"Cmt da {Name}";
    public static string Treasury => $"Tesouraria da {Name}";
}
