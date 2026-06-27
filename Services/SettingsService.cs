using System.Text.Json.Nodes;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;

    public SettingsService(AppPaths paths, JsonFileService json)
    {
        _paths = paths;
        _json = json;
    }

    public async Task<UiProfile> LoadProfileAsync()
    {
        var root = await _json.LoadNodeAsync(_paths.UiConfigFile) as JsonObject ?? new JsonObject();
        return new UiProfile
        {
            Rank = ReadString(root, "posto_graduacao", "3º Sgt"),
            Operator = ReadString(root, "operador", "Operador"),
            Function = ReadString(root, "funcao", "Furriel"),
            Organization = ReadString(root, "om", "Organização Militar"),
            CommanderName = ReadString(root, "comandante_nome", string.Empty),
            CommanderRank = ReadString(root, "comandante_posto_funcao", string.Empty),
            LogoPath = ReadString(root, "logo", string.Empty),
            LegacyProjectRoot = ReadString(root, "legacy_project_root", string.Empty),
            OrganizationCatalog = ReadStringList(root["oms_salvas"], ReadString(root, "om", "Organização Militar")),
            OrganizationImages = ReadStringDictionary(root["imagens_om"])
        };
    }

    public async Task SaveProfileAsync(UiProfile profile)
    {
        var root = await _json.LoadNodeAsync(_paths.UiConfigFile) as JsonObject ?? new JsonObject();
        root["posto_graduacao"] = profile.Rank;
        root["operador"] = profile.Operator;
        root["funcao"] = profile.Function;
        root["om"] = profile.Organization;
        root["comandante_nome"] = profile.CommanderName;
        root["comandante_posto_funcao"] = profile.CommanderRank;
        root["logo"] = profile.LogoPath;
        root["legacy_project_root"] = profile.LegacyProjectRoot;
        root["oms_salvas"] = new JsonArray(profile.OrganizationCatalog.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Select(x => (JsonNode?)x).ToArray());
        var images = new JsonObject(); foreach (var kv in profile.OrganizationImages) images[kv.Key] = kv.Value; root["imagens_om"] = images;
        await _json.SaveNodeAsync(_paths.UiConfigFile, root);
    }

    public async Task<WindowStateData> LoadWindowStateAsync()
    {
        var root = await _json.LoadNodeAsync(_paths.AppSettingsFile) as JsonObject ?? new JsonObject();
        var wpf = root["wpf_main_window"] as JsonObject;
        return SanitizeWindowState(new WindowStateData
        {
            Left = ReadDouble(wpf, "left", 0),
            Top = ReadDouble(wpf, "top", 0),
            Width = ReadDouble(wpf, "width", 1180),
            Height = ReadDouble(wpf, "height", 820),
            Maximized = ReadBool(wpf, "maximized", false),
            NavigationCollapsed = ReadBool(root, "nav_collapsed", false),
            UiScale = ReadDouble(root, "font_scale", 1.0)
        });
    }

    public async Task SaveWindowStateAsync(WindowStateData state)
    {
        state = SanitizeWindowState(state);
        var root = await _json.LoadNodeAsync(_paths.AppSettingsFile) as JsonObject ?? new JsonObject();
        root["nav_collapsed"] = state.NavigationCollapsed;
        root["font_scale"] = state.UiScale;
        root["wpf_main_window"] = new JsonObject
        {
            ["left"] = state.Left,
            ["top"] = state.Top,
            ["width"] = state.Width,
            ["height"] = state.Height,
            ["maximized"] = state.Maximized
        };
        await _json.SaveNodeAsync(_paths.AppSettingsFile, root);
    }

    public async Task<Dictionary<string, string>> LoadHotkeysAsync()
    {
        var defaults = DefaultHotkeys();
        var root = await _json.LoadNodeAsync(_paths.AppSettingsFile) as JsonObject;
        var hotkeys = root?["hotkeys"] as JsonObject;
        if (hotkeys is null) return defaults;

        foreach (var kv in hotkeys)
        {
            var value = ReadStringValue(kv.Value, string.Empty);
            if (!string.IsNullOrWhiteSpace(value)) defaults[kv.Key] = value;
        }
        return defaults;
    }

    public async Task SaveHotkeysAsync(Dictionary<string, string> hotkeys)
    {
        var root = await _json.LoadNodeAsync(_paths.AppSettingsFile) as JsonObject ?? new JsonObject();
        var obj = new JsonObject();
        foreach (var kv in hotkeys) obj[kv.Key] = kv.Value;
        root["hotkeys"] = obj;
        await _json.SaveNodeAsync(_paths.AppSettingsFile, root);
    }

    private static WindowStateData SanitizeWindowState(WindowStateData state)
    {
        state.Left = FiniteOr(state.Left, 0);
        state.Top = FiniteOr(state.Top, 0);
        state.Width = Math.Clamp(FiniteOr(state.Width, 1180), 480, 10000);
        state.Height = Math.Clamp(FiniteOr(state.Height, 820), 360, 10000);
        state.UiScale = Math.Clamp(FiniteOr(state.UiScale, 1.0), 0.80, 1.40);
        return state;
    }

    private static double FiniteOr(double value, double fallback)
        => double.IsFinite(value) ? value : fallback;

    private static List<string> ReadStringList(JsonNode? node, string current)
    {
        var result = node is JsonArray array ? array.Select(x => x?.ToString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [];
        if (!string.IsNullOrWhiteSpace(current) && !result.Contains(current, StringComparer.OrdinalIgnoreCase)) result.Insert(0, current);
        if (result.Count == 0) result.Add("4ª Cia PE");
        return result;
    }

    private static Dictionary<string,string> ReadStringDictionary(JsonNode? node)
    {
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject obj) foreach (var kv in obj) if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value?.ToString())) result[kv.Key] = kv.Value!.ToString();
        return result;
    }

    private static string ReadString(JsonObject? obj, string key, string fallback)
        => ReadStringValue(obj?[key], fallback);

    private static string ReadStringValue(JsonNode? node, string fallback)
    {
        try
        {
            if (node is null) return fallback;
            if (node is JsonValue value)
            {
                if (value.TryGetValue<string>(out var text)) return text ?? fallback;
                return value.ToString();
            }
        }
        catch { }
        return fallback;
    }

    private static double ReadDouble(JsonObject? obj, string key, double fallback)
    {
        try
        {
            if (obj?[key] is not JsonValue value) return fallback;
            if (value.TryGetValue<double>(out var number) && double.IsFinite(number)) return number;
            if (value.TryGetValue<decimal>(out var decimalNumber)) return (double)decimalNumber;
            if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number))
                return number;
        }
        catch { }
        return fallback;
    }

    private static bool ReadBool(JsonObject? obj, string key, bool fallback)
    {
        try
        {
            if (obj?[key] is not JsonValue value) return fallback;
            if (value.TryGetValue<bool>(out var boolean)) return boolean;
            if (bool.TryParse(value.ToString(), out boolean)) return boolean;
            if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number != 0;
        }
        catch { }
        return fallback;
    }

    public static Dictionary<string, string> DefaultHotkeys() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["cadastro"] = "Ctrl+N",
        ["listar"] = "Ctrl+L",
        ["boletim"] = "Ctrl+B",
        ["soldos"] = "Ctrl+S",
        ["aux_transporte"] = "Ctrl+T",
        ["grat_representacao"] = "Ctrl+G",
        ["relacao_pessoal"] = "Ctrl+R",
        ["ajuste_contas"] = "Ctrl+J",
        ["pensao_judicial"] = "Ctrl+P",
        ["inconsistencia_bancaria"] = "Ctrl+Alt+I",
        ["bizurometro_sped"] = "Ctrl+Alt+B",
        ["lic_transf"] = "Ctrl+Y",
        ["num_extenso"] = "Ctrl+Alt+E",
        ["calculadora"] = "F2",
        ["refresh_dashboard"] = "F5",
        ["font_up"] = "Ctrl+=",
        ["font_down"] = "Ctrl+-",
        ["font_reset"] = "Ctrl+0",
        ["gerenciar_atalhos"] = "Ctrl+Alt+P",
        ["sair"] = "Alt+F4"
    };
}
