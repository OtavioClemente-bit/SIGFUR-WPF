using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace SIGFUR.Wpf.Services;

public sealed record ThemePalette(
    string Id,
    string DisplayName,
    string Description,
    string Primary,
    string PrimaryHover,
    string PrimaryPressed,
    string PrimaryDark,
    string PrimaryDarker,
    string PrimarySoft,
    string PrimarySurface,
    string Accent,
    string AppBackground,
    string Surface,
    string SurfaceElevated,
    string SurfaceAlt,
    string SurfaceHover,
    string Border,
    string BorderStrong,
    string Divider,
    string Text,
    string Muted,
    string SubtleText,
    string Sidebar,
    string SidebarHover,
    string SidebarSelected,
    string SidebarText,
    string SidebarMuted);

/// <summary>
/// Aplica a identidade visual inteira do SIGFUR por recursos globais do WPF.
///
/// A versão anterior alterava apenas os recursos Color. Alguns brushes definidos em
/// dicionários mesclados permaneciam com a cor antiga. Esta implementação substitui
/// simultaneamente Color, SolidColorBrush e o gradiente institucional no nível da
/// aplicação, garantindo atualização imediata de todas as janelas que usam
/// DynamicResource.
/// </summary>
public sealed class ThemeService
{
    private readonly string _settingsFile;
    private readonly JsonFileService _json;

    public ThemeService(AppPaths paths, JsonFileService json)
        : this(paths.ThemeSettingsFile, json)
    {
    }

    public ThemeService(string themeSettingsFile, JsonFileService json)
    {
        _settingsFile = Path.GetFullPath(themeSettingsFile);
        _json = json;
    }

    public event EventHandler? ThemeChanged;
    public string CurrentThemeId { get; private set; } = "institutional-blue";
    public ThemePalette Current => Themes.First(x => x.Id == CurrentThemeId);
    public IReadOnlyList<ThemePalette> AvailableThemes => Themes;

    public async Task InitializeAsync()
    {
        var root = await _json.LoadNodeAsync(_settingsFile) as JsonObject;
        var id = "institutional-blue";
        try
        {
            if (root?["theme"] is JsonValue value && value.TryGetValue<string>(out var saved) && !string.IsNullOrWhiteSpace(saved))
                id = saved;
        }
        catch
        {
            // Configuração antiga ou corrompida: o tema padrão mantém o aplicativo abrindo.
        }
        Apply(id, raiseEvent: false);
    }

    public async Task ApplyAndSaveAsync(string themeId)
    {
        Apply(themeId, raiseEvent: true);
        await _json.SaveNodeAsync(_settingsFile, new JsonObject
        {
            ["theme"] = CurrentThemeId,
            ["updated_at"] = DateTime.Now.ToString("O")
        });
    }

    public void Apply(string themeId, bool raiseEvent = true)
    {
        var theme = Themes.FirstOrDefault(x => x.Id.Equals(themeId, StringComparison.OrdinalIgnoreCase))
                    ?? Themes[0];
        CurrentThemeId = theme.Id;

        var resources = Application.Current?.Resources;
        if (resources is null) return;

        SetToken(resources, "PrimaryColor", "PrimaryBrush", theme.Primary);
        SetToken(resources, "PrimaryHoverColor", "PrimaryHoverBrush", theme.PrimaryHover);
        SetToken(resources, "PrimaryPressedColor", "PrimaryPressedBrush", theme.PrimaryPressed);
        SetToken(resources, "PrimaryDarkColor", "PrimaryDarkBrush", theme.PrimaryDark);
        SetToken(resources, "PrimaryDarkerColor", "PrimaryDarkerBrush", theme.PrimaryDarker);
        SetToken(resources, "PrimarySoftColor", "PrimarySoftBrush", theme.PrimarySoft);
        SetToken(resources, "PrimarySurfaceColor", "PrimarySurfaceBrush", theme.PrimarySurface);
        SetToken(resources, "AccentColor", "AccentBrush", theme.Accent);

        SetToken(resources, "AppBackgroundColor", "AppBackgroundBrush", theme.AppBackground);
        SetToken(resources, "SurfaceColor", "SurfaceBrush", theme.Surface);
        SetToken(resources, "SurfaceElevatedColor", "SurfaceElevatedBrush", theme.SurfaceElevated);
        SetToken(resources, "SurfaceAltColor", "SurfaceAltBrush", theme.SurfaceAlt);
        SetToken(resources, "SurfaceHoverColor", "SurfaceHoverBrush", theme.SurfaceHover);
        SetToken(resources, "BorderColor", "BorderBrush", theme.Border);
        SetToken(resources, "BorderStrongColor", "BorderStrongBrush", theme.BorderStrong);
        SetToken(resources, "DividerColor", "DividerBrush", theme.Divider);

        SetToken(resources, "TextColor", "TextBrush", theme.Text);
        SetToken(resources, "MutedColor", "MutedBrush", theme.Muted);
        SetToken(resources, "SubtleTextColor", "SubtleTextBrush", theme.SubtleText);

        SetToken(resources, "SidebarColor", "SidebarBrush", theme.Sidebar);
        SetToken(resources, "SidebarHoverColor", "SidebarHoverBrush", theme.SidebarHover);
        SetToken(resources, "SidebarSelectedColor", "SidebarSelectedBrush", theme.SidebarSelected);
        SetToken(resources, "SidebarTextColor", "SidebarTextBrush", theme.SidebarText);
        SetToken(resources, "SidebarMutedColor", "SidebarMutedBrush", theme.SidebarMuted);

        resources["PrimaryGradientBrush"] = CreateGradient(theme.PrimaryDarker, theme.PrimaryDark, theme.Primary);

        // Força a árvore visual aberta a refazer a consulta dos DynamicResources.
        var application = Application.Current;
        if (application is not null)
        {
            foreach (Window window in application.Windows)
            {
                try
                {
                    window.InvalidateVisual();
                    window.UpdateLayout();
                }
                catch
                {
                    // Uma janela em processo de fechamento não deve impedir a troca de tema.
                }
            }
        }

        if (raiseEvent) ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void SetToken(ResourceDictionary resources, string colorKey, string brushKey, string value)
    {
        var color = ParseColor(value);
        resources[colorKey] = color;
        resources[brushKey] = CreateBrush(color);
    }

    private static Color ParseColor(string value)
    {
        var converted = ColorConverter.ConvertFromString(value);
        return converted is Color color ? color : Colors.Transparent;
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateGradient(string darker, string dark, string primary)
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        gradient.GradientStops.Add(new GradientStop(ParseColor(darker), 0));
        gradient.GradientStops.Add(new GradientStop(ParseColor(dark), 0.48));
        gradient.GradientStops.Add(new GradientStop(ParseColor(primary), 1));
        if (gradient.CanFreeze) gradient.Freeze();
        return gradient;
    }

    private static readonly List<ThemePalette> Themes =
    [
        new(
            "institutional-blue", "Azul Institucional", "Identidade oficial do SIGFUR, clara, moderna e equilibrada.",
            "#1565C0", "#0F59AD", "#0B4A91", "#0B3D91", "#072A66", "#E8F2FD", "#F3F8FE", "#4DA3F5",
            "#EEF3F8", "#FFFFFF", "#FFFFFF", "#F7F9FC", "#F0F5FA", "#DCE5EF", "#C8D6E5", "#E8EEF5",
            "#102A43", "#61758A", "#8294A6", "#0B315F", "#134578", "#1C5C98", "#F4F9FF", "#B8D2EB"),
        new(
            "navy-command", "Azul Comando", "Azul-marinho mais profundo, com contraste institucional forte.",
            "#2563A6", "#1D5088", "#163F6D", "#12365C", "#071F38", "#E4EFF9", "#F0F6FC", "#64B5F6",
            "#E9F0F6", "#FFFFFF", "#FFFFFF", "#F4F7FA", "#EAF0F5", "#D3DEE8", "#B7C8D8", "#DFE7EE",
            "#10283D", "#567086", "#7D92A3", "#071F38", "#103A61", "#175082", "#F7FBFF", "#AFCBE2"),
        new(
            "graphite-executive", "Grafite Executivo", "Visual corporativo neutro, com grafite e azul de destaque.",
            "#356FAD", "#2B5D92", "#234A74", "#26384B", "#172431", "#E9F0F7", "#F4F7FA", "#68A2D8",
            "#E9EDF1", "#FCFDFE", "#FFFFFF", "#F2F4F6", "#E8ECEF", "#D2D9E0", "#B8C2CC", "#DDE3E8",
            "#16212B", "#53616D", "#798692", "#1D2731", "#2B3844", "#3A4A59", "#F8FAFC", "#B8C4CE"),
        new(
            "operational-green", "Verde Operacional", "Verde sóbrio para diferenciar ambientes e rotinas operacionais.",
            "#23815E", "#1C6A4D", "#16533D", "#174D3B", "#0B3327", "#E1F3EB", "#EFF9F4", "#55B68D",
            "#E9F2EE", "#FFFFFF", "#FFFFFF", "#F3F8F5", "#EAF3EE", "#D2E2DA", "#B7CFC2", "#DDE9E3",
            "#102920", "#536F63", "#789087", "#0D392C", "#174F3D", "#20664F", "#F5FCF8", "#B4D5C6"),
        new(
            "sand-administrative", "Areia Administrativo", "Visual claro em tons de areia, bom para leitura prolongada e salas muito iluminadas.",
            "#B36B24", "#98591C", "#7D4614", "#7A4317", "#4E2A0B", "#FFF1DF", "#FFF8EF", "#E59A4A",
            "#F4EFE8", "#FFFFFF", "#FFFFFF", "#FAF6F0", "#F1E8DD", "#DDCDB9", "#CBB79F", "#E7DBCC",
            "#2E2419", "#6F6255", "#938678", "#4E2A0B", "#734010", "#95591E", "#FFF9F1", "#E8C39B"),
        new(
            "burgundy-staff", "Vinho Estado-Maior", "Tema sóbrio em vinho, com destaque forte sem perder leitura em tabelas.",
            "#9B2F45", "#84283A", "#6D2030", "#6A2132", "#43131E", "#F8E6EA", "#FFF2F5", "#D86D82",
            "#F2EBED", "#FFFFFF", "#FFFFFF", "#F8F3F4", "#EFE4E7", "#DCCBD0", "#C7B1B8", "#E6DCE0",
            "#2B1720", "#6C5860", "#8F7D84", "#43131E", "#642033", "#852A42", "#FFF7F9", "#E6B9C4"),
        new(
            "purple-tech", "Roxo Técnico", "Aparência moderna para módulos de conferência, automação e assistente.",
            "#6D4DB3", "#5C4099", "#4A337D", "#403071", "#271B49", "#EEE8FB", "#F7F3FF", "#9C7AE6",
            "#EFECF6", "#FFFFFF", "#FFFFFF", "#F7F5FB", "#ECE8F5", "#D8D0E8", "#C1B4D7", "#E2DCEB",
            "#211A2E", "#625873", "#857C96", "#271B49", "#3A2B69", "#4F3B8B", "#FAF7FF", "#CFC2EC"),
        new(
            "slate-clean", "Cinza Limpo", "Tema claro, neutro e discreto para quem prefere menos cor na rotina diária.",
            "#4B6B8A", "#3E5A75", "#33495F", "#2E4358", "#1D2C3A", "#EAF0F5", "#F5F8FA", "#7FA6C9",
            "#EDF1F4", "#FFFFFF", "#FFFFFF", "#F6F8FA", "#ECEFF2", "#D7DEE5", "#BEC9D4", "#E1E6EB",
            "#16232E", "#5B6670", "#808A94", "#1D2C3A", "#2E4358", "#3F5B76", "#F8FAFC", "#C2CFDA")
    ];
}
