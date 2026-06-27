using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;
using SIGFUR.Wpf.Controls;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed partial class BulletinService
{
    private static readonly HashSet<string> AutoFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "POSTO", "POSTO_ABREV", "NOME", "NOME_COMPLETO", "NOME_DESTACADO", "NOME_GUERRA",
        "CPF", "PREC_CP", "IDT", "EMAIL", "ESCOLARIDADE", "TELEFONE", "ENDERECO", "CEP",
        "BANCO", "AGENCIA", "CONTA", "TEMPO_SERVICO", "MILITAR_FORMATADO",
        "BI_REFERENCIA", "REFERENCIA_BOLETIM", "PUBLICACAO_BI", "BOLETIM_REFERENCIA",
        "BI_TIPO", "BI_ORIGEM", "BI_NUMERO", "NUM_BI", "BI_NUMERO_COMPLETO",
        "DATA_BI", "DATA_PUBLICACAO_BI", "DATA_PUBLICACAO", "DATA_BI_ABREV",
        "DATA_PUBLICACAO_BI_ABREV", "ADT_REFERENCIA", "NUM_ADT", "DATA_ADT",
        "DATA_HOJE", "DATA_ATUAL", "DATA_HOJE_BR", "DATA_HOJE_ABREV", "DATA_HOJE_EXTENSO",
        "DIA_ATUAL", "MES_ATUAL", "MES_ATUAL_NOME", "MES_ATUAL_ABREV", "MES_ATUAL_NOME_ANO",
        "MES_ATUAL_ABREV_ANO", "MES_ATUAL_NUMERO", "ANO_ATUAL"
    };
    private static readonly HashSet<string> AutoFieldKeys = AutoFields.Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;
    private readonly List<CodomCatalogEntry> _codomCatalog;

    public BulletinService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths;
        _json = json;
        _log = log;
        _codomCatalog = LoadCodomCatalog(paths.BulletinCodomCatalogFile);
    }

    public async Task<List<BulletinTemplate>> LoadTemplatesAsync()
    {
        var builtIn = NormalizeTemplateMap(await ReadTemplateMapAsync(_paths.BulletinDefaultTemplatesFile));
        var saved = NormalizeTemplateMap(await ReadTemplateMapAsync(_paths.BulletinTemplatesFile));
        // Notas que pertencem a módulos com cálculo próprio não ficam mais soltas no Boletim.
        // Isso evita publicar texto incompleto: AT usa a carteira/rotas, férias usa Plano de Férias
        // e Ajuste de Contas usa o cálculo de rubricas do próprio módulo.
        RemoveModuleOwnedTemplateNames(builtIn);
        RemoveModuleOwnedTemplateNames(saved);
        RemoveDeprecatedPaymentTransferTemplates(builtIn);
        RemoveDeprecatedPaymentTransferTemplates(saved);
        foreach (var item in saved)
        {
            // Um modelo salvo em branco não pode apagar o conteúdo padrão. Isso
            // também recupera instalações antigas em que o JSON foi gravado vazio.
            if (!string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
                builtIn[item.Key] = item.Value;
        }
        EnsurePaymentTransferCodomFields(builtIn);
        if (builtIn.Count == 0)
        {
            builtIn["Boletim — modelo básico"] =
                "Publique-se o que se segue:\n\n[[ITEM]][[POSTO_ABREV]] [[NOME]]\nPrec-CP [[PREC_CP]] CPF [[CPF]][[/ITEM]]";
        }

        var prefs = await LoadPreferencesAsync();
        var order = prefs.TemplateOrder
            .Select((name, index) => (name: NormalizeTemplateName(name), index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);

        var builtInNames = new HashSet<string>(NormalizeTemplateMap(await ReadTemplateMapAsync(_paths.BulletinDefaultTemplatesFile)).Keys, StringComparer.OrdinalIgnoreCase);
        var result = builtIn.Select((x, index) => new BulletinTemplate
        {
            Name = x.Key,
            Text = x.Value,
            IsBuiltIn = builtInNames.Contains(x.Key),
            Order = order.TryGetValue(x.Key, out var stored) ? stored : 10000 + index
        }).OrderBy(x => x.Order).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        Renumber(result);
        return result;
    }

    public async Task SaveTemplatesAsync(IEnumerable<BulletinTemplate> templates)
    {
        var ordered = templates.OrderBy(x => x.Order).ToList();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in ordered)
        {
            template.Name = NormalizeTemplateName(template.Name);
            if (!string.IsNullOrWhiteSpace(template.Name))
                map[template.Name] = template.Text ?? string.Empty;
        }
        await _json.SaveAsync(_paths.BulletinTemplatesFile, map);
        var prefs = await LoadPreferencesAsync();
        prefs.TemplateOrder = ordered.Select(x => x.Name).ToList();
        await SavePreferencesAsync(prefs);
    }

    public async Task<BulletinPreferences> LoadPreferencesAsync()
    {
        var preferences = await _json.LoadAsync<BulletinPreferences>(_paths.BulletinPreferencesFile) ?? new BulletinPreferences();
        try
        {
            if (File.Exists(_paths.BulletinLegacyPreferencesFile))
            {
                var legacy = await _json.LoadNodeAsync(_paths.BulletinLegacyPreferencesFile) as JsonObject;
                if (legacy is not null)
                {
                    if (string.IsNullOrWhiteSpace(preferences.LastTemplate)) preferences.LastTemplate = legacy["last_tipo"]?.ToString().Trim('"') ?? string.Empty;
                    if (legacy["tipos_boletim_order"] is JsonArray order && preferences.TemplateOrder.Count == 0)
                        preferences.TemplateOrder = order.Select(x => x?.ToString().Trim('"') ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                    if (legacy["ordem_disponiveis_mode"] is not null && preferences.AvailableSortMode == "Ordem salva (Listar Militares)")
                        preferences.AvailableSortMode = legacy["ordem_disponiveis_mode"]!.ToString().Trim('"');
                }
            }
            if (File.Exists(_paths.BulletinLegacyFormPreferencesFile) && preferences.FormValues.Count == 0)
            {
                var legacyForms = await _json.LoadNodeAsync(_paths.BulletinLegacyFormPreferencesFile) as JsonObject;
                if (legacyForms is not null)
                {
                    foreach (var model in legacyForms)
                    {
                        if (model.Value is not JsonObject fields) continue;
                        preferences.FormValues[model.Key] = fields.ToDictionary(x => x.Key, x => x.Value?.ToString().Trim('"') ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
        }
        catch (Exception ex) { await _log.WriteAsync("Falha ao importar preferências legadas do boletim.", ex); }
        NormalizePreferenceTemplateNames(preferences);
        return preferences;
    }

    public Task SavePreferencesAsync(BulletinPreferences preferences)
    {
        NormalizePreferenceTemplateNames(preferences);
        return _json.SaveAsync(_paths.BulletinPreferencesFile, preferences);
    }

    public async Task<Dictionary<string, string>> LoadGlobalKeysAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _paths.BulletinGlobalKeyFiles)
        {
            try
            {
                var node = await _json.LoadNodeAsync(path);
                if (node is not JsonObject obj) continue;
                foreach (var pair in obj)
                {
                    if (pair.Key.StartsWith('_') || pair.Value is null) continue;
                    var value = pair.Value.ToString().Trim('"');
                    if (!string.IsNullOrWhiteSpace(value)) result[pair.Key] = value;
                }
            }
            catch (Exception ex) { await _log.WriteAsync($"Falha ao ler chaves do boletim: {path}", ex); }
        }
        AddAutomaticDateKeys(result, DateTime.Today);
        return result;
    }

    public async Task SaveGlobalKeysAsync(Dictionary<string, string> values)
    {
        var clean = values.Where(x => !string.IsNullOrWhiteSpace(x.Key) && !IsAutomaticKey(x.Key))
            .ToDictionary(x => x.Key.Trim(), x => x.Value?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        await _json.SaveAsync(_paths.BulletinManualKeysFile, clean);
    }

    public Task SaveBulletinReferenceKeysAsync(IReadOnlyDictionary<string, string> values)
    {
        var clean = values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        return _json.SaveAsync(Path.Combine(_paths.DataDirectory, "boletim_chaves_boletim.json"), clean);
    }

    public async Task<Dictionary<string, string>> LoadManualKeysAsync()
    {
        try
        {
            var values = await _json.LoadAsync<Dictionary<string, string>>(_paths.BulletinManualKeysFile) ?? [];
            return values.Where(x => !string.IsNullOrWhiteSpace(x.Key) && !IsAutomaticKey(x.Key))
                .ToDictionary(x => x.Key.Trim(), x => x.Value?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao carregar as chaves manuais do Boletim.", ex);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static bool IsAutomaticKey(string? key) => AutoFieldKeys.Contains(NormalizeKey(key));

    public static bool IsMeaningfulFieldValue(string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalizedValue = NormalizeKey(value);
        if (normalizedValue is "PREENCHER" or "INFORMAR" or "NAOSEAPLICA") return false;
        return !normalizedValue.Equals(NormalizeKey(key), StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> SuggestOptions(string? key, string? templateName = null)
    {
        var normalized = NormalizeKey(key);
        var template = NormalizeKey(templateName);
        if (string.IsNullOrWhiteSpace(normalized)) return [];

        if (normalized.Contains("TIPOCURSO", StringComparison.OrdinalIgnoreCase))
            return ["Formação", "Especialização", "Aperfeiçoamento", "Altos Estudos Categoria I", "Altos Estudos Categoria II"];
        if (normalized.Contains("ACAOHABILITACAO", StringComparison.OrdinalIgnoreCase))
            return ["implantado", "alterado"];
        if (normalized.Contains("PERCENTUALCURSO", StringComparison.OrdinalIgnoreCase) || normalized.Contains("PERCENTUALHABILITACAO", StringComparison.OrdinalIgnoreCase))
            return ["12%", "16%", "20%", "25%", "30%", "35%", "40%", "45%", "73%"];
        if (normalized.Contains("DECISAOJUDICIAL", StringComparison.OrdinalIgnoreCase))
            return ["Acórdão", "Liminar", "Sentença", "Transitado em Julgado", "Tutela Antecipada"];
        if (normalized.Contains("TIPOPENSAO", StringComparison.OrdinalIgnoreCase))
            return ["Pensão Judicial", "Pensão Extrajudicial"];
        if (normalized.Contains("TRIBUNAL", StringComparison.OrdinalIgnoreCase))
            return ["Vara Estadual", "Vara Federal", "TJ", "TRF", "STJ", "STF", "Outro"];
        if (normalized.Contains("PARENTESCO", StringComparison.OrdinalIgnoreCase) || normalized.Contains("GRAUGUARDA", StringComparison.OrdinalIgnoreCase))
            return ["Filho(a)", "Cônjuge", "Companheiro(a)", "Enteado(a)", "Menor sob guarda/tutela", "Pai", "Mãe", "Irmão(ã)", "Detentor(a) da guarda", "Outro"];
        if (normalized.Contains("FINALIDADEDEPENDENCIA", StringComparison.OrdinalIgnoreCase))
            return ["Salário-Família e Imposto de Renda", "Salário-Família, FUSEx e Imposto de Renda", "FUSEx", "Imposto de Renda", "Assentamentos"];
        if (normalized.Contains("CODIGOSIPPESPENSAO", StringComparison.OrdinalIgnoreCase))
            return ["Pensão alimentícia judicial - implantação", "Pensão alimentícia extrajudicial - implantação", "Alteração de pensão", "Exclusão de pensão", "Desconto/indenização judicial"];
        if (normalized.Contains("MOTIVOEXCLUSAO", StringComparison.OrdinalIgnoreCase))
            return ["Ordem Judicial", "Óbito do Alimentado"];
        if (normalized.Contains("INCIDE", StringComparison.OrdinalIgnoreCase) || normalized.Contains("INCIDENCIA", StringComparison.OrdinalIgnoreCase))
            return ["Incide", "Não incide", "Percentual", "Valor fixo"];
        if (normalized.Contains("REGRA", StringComparison.OrdinalIgnoreCase) || normalized.Contains("FORMACALCULO", StringComparison.OrdinalIgnoreCase) || normalized.Contains("FORMADESCONTO", StringComparison.OrdinalIgnoreCase))
            return ["Valor fixo", "Percentual sobre rendimentos líquidos", "Percentual sobre soldo", "Fórmula de cálculo judicial", "Desconto em parcela única", "Parcelado"];
        if (normalized.Contains("BANCO", StringComparison.OrdinalIgnoreCase))
            return ["001 - Banco do Brasil S.A", "033 - Santander", "041 - Banrisul", "104 - Caixa Econômica Federal", "237 - Bradesco", "341 - Itaú Unibanco S.A", "756 - Sicoob"];
        if (normalized.Contains("CODIGOSAQUE", StringComparison.OrdinalIgnoreCase) || normalized.Contains("CODIGOATRASADO", StringComparison.OrdinalIgnoreCase) || (template.Contains("AUXILIOALIMENTACAO", StringComparison.OrdinalIgnoreCase) && normalized.Contains("CODIGO", StringComparison.OrdinalIgnoreCase)))
            return ["A58 - Aux Alim 1X", "A53 - Aux Alim 5X", "A52 - Aux Alim 10X", "A48 - Aux Alim atrasado 1X", "A43 - Aux Alim atrasado 5X", "A42 - Aux Alim atrasado 10X"];
        if (template.Contains("PNR", StringComparison.OrdinalIgnoreCase) && (normalized.Contains("TIPOPNR", StringComparison.OrdinalIgnoreCase) || normalized.Contains("TIPOIMOVEL", StringComparison.OrdinalIgnoreCase) || normalized.Contains("NATUREZAPNR", StringComparison.OrdinalIgnoreCase)))
            return ["Casa", "Apartamento"];
        if (template.Contains("PNR", StringComparison.OrdinalIgnoreCase) && (normalized.Contains("PERCENTUAL", StringComparison.OrdinalIgnoreCase) || normalized.Contains("PERCENTUALVALOR", StringComparison.OrdinalIgnoreCase)))
            return ["3,5%", "5%"];
        if (normalized.Contains("TIPOTRANSFERENCIA", StringComparison.OrdinalIgnoreCase))
            return ["Apresentação nesta OM", "Desligamento para outra OM"];
        if (normalized.Contains("OMDESTINO", StringComparison.OrdinalIgnoreCase) || normalized.Contains("OMORIGEM", StringComparison.OrdinalIgnoreCase) || normalized.Contains("OMPAGAMENTO", StringComparison.OrdinalIgnoreCase))
            return _codomCatalog.Select(x => x.Display).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (normalized.Contains("CODOM", StringComparison.OrdinalIgnoreCase))
            return _codomCatalog.Select(x => x.Codom).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        if (normalized.Contains("TIPOEXERCICIOANTERIOR", StringComparison.OrdinalIgnoreCase) || normalized.Contains("CODIGORUBRICA", StringComparison.OrdinalIgnoreCase))
            return ExercisePreviousDefaults.PreviousExerciseTypes.ToList();
        if (normalized.Contains("SIMNAO", StringComparison.OrdinalIgnoreCase) || normalized is "TEMOUTRASPENSOES" or "TEMOUTRAPENSAO")
            return ["Sim", "Não"];
        return [];
    }

    public bool TryResolveCodom(string? organization, out string codom)
    {
        codom = string.Empty;
        var raw = organization?.Trim() ?? string.Empty;
        var wanted = NormalizeKey(raw);
        if (string.IsNullOrWhiteSpace(wanted)) return false;

        // 1) Prioriza a SIGLA/descrição digitada. Isso evita erro quando existe texto legado
        // como "4ª Cia PE - CODOM 1950/4ª RM": a sigla correta resolve para 037515.
        var exact = _codomCatalog.FirstOrDefault(x => NormalizeKey(x.Display) == wanted)
                    ?? _codomCatalog.FirstOrDefault(x => NormalizeKey(x.Sigla) == wanted)
                    ?? _codomCatalog.FirstOrDefault(x => NormalizeKey(x.Codom) == wanted)
                    ?? _codomCatalog.FirstOrDefault(x => WantedContainsSigla(wanted, x) || SiglaContainsWanted(wanted, x));
        if (exact is not null && !string.IsNullOrWhiteSpace(exact.Codom))
        {
            codom = exact.Codom;
            return true;
        }

        // 2) Se foi digitado ou colado só um CODOM real, aceita o número.
        foreach (Match match in Regex.Matches(raw, @"\b\d{5,6}\b"))
        {
            var digits = match.Value.Trim();
            var byCode = _codomCatalog.FirstOrDefault(x => x.Codom.Equals(digits, StringComparison.OrdinalIgnoreCase));
            if (byCode is not null)
            {
                codom = byCode.Codom;
                return true;
            }
        }

        // 3) Busca por palavras, preservando utilidade quando o operador digita só parte do nome da OM.
        var tokens = Regex.Matches(RemoveAccents(raw).ToUpperInvariant(), @"[A-Z0-9]{2,}")
            .Select(m => m.Value)
            .Where(x => x is not "CODOM" and not "CODUG" and not "RM" and not "CML" and not "CGCFEX")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tokens.Count == 0) return false;

        var scored = _codomCatalog
            .Select(item => (item, score: ScoreCodomMatch(tokens, item)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.item.Situation == "A" ? 0 : x.item.Situation == "N" ? 1 : 2)
            .ThenBy(x => x.item.Sigla.Length)
            .FirstOrDefault();
        if (scored.item is null || string.IsNullOrWhiteSpace(scored.item.Codom)) return false;
        codom = scored.item.Codom;
        return true;

        static bool WantedContainsSigla(string wanted, CodomCatalogEntry item)
        {
            var sigla = NormalizeKey(item.Sigla);
            return sigla.Length >= 4 && wanted.Contains(sigla, StringComparison.OrdinalIgnoreCase);
        }

        static bool SiglaContainsWanted(string wanted, CodomCatalogEntry item)
        {
            var sigla = NormalizeKey(item.Sigla);
            return wanted.Length >= 4 && sigla.Contains(wanted, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int ScoreCodomMatch(IReadOnlyList<string> tokens, CodomCatalogEntry item)
    {
        var sigla = RemoveAccents(item.Sigla).ToUpperInvariant();
        var display = RemoveAccents(item.Display).ToUpperInvariant();
        var normalizedSigla = NormalizeKey(item.Sigla);
        var normalizedDisplay = NormalizeKey(item.Display);
        var score = 0;
        foreach (var token in tokens)
        {
            var n = NormalizeKey(token);
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (normalizedSigla.Equals(n, StringComparison.OrdinalIgnoreCase)) score += 200;
            else if (normalizedSigla.Contains(n, StringComparison.OrdinalIgnoreCase)) score += 70;
            else if (normalizedDisplay.Contains(n, StringComparison.OrdinalIgnoreCase)) score += 25;
            if (sigla.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 20;
            else if (display.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 8;
        }
        if (item.Situation == "A") score += 8;
        if (item.Situation == "N") score += 4;
        return score;
    }

    private static List<CodomCatalogEntry> LoadCodomCatalog(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("items", out var items)) return [];
            return items.EnumerateArray().Select(item => new CodomCatalogEntry
            {
                Codug = Read(item, "codug"), Codom = Read(item, "codom"), Sigla = Read(item, "sigla"),
                Rm = Read(item, "rm"), City = Read(item, "cidade_estado"), Situation = Read(item, "sit")
            }).Where(x => !string.IsNullOrWhiteSpace(x.Codom) && !string.IsNullOrWhiteSpace(x.Sigla)).ToList();

            static string Read(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
        }
        catch { return []; }
    }

    private sealed class CodomCatalogEntry
    {
        public string Codug { get; init; } = string.Empty;
        public string Codom { get; init; } = string.Empty;
        public string Sigla { get; init; } = string.Empty;
        public string Rm { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string Situation { get; init; } = string.Empty;
        public string Display => $"{Sigla} — {Rm} — {City} — CODOM {Codom} — CODUG {Codug}";
    }

    public Dictionary<string, string> EnrichFormValues(string? templateName, IReadOnlyDictionary<string, string>? formValues)
    {
        var result = (formValues ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        ApplyAuxilioAlimentacaoCalculations(templateName, result);
        return result;
    }

    private static void ApplyAuxilioAlimentacaoCalculations(string? templateName, IDictionary<string, string> values)
    {
        var template = NormalizeKey(templateName);
        if (!template.Contains("AUXILIOALIMENTACAO", StringComparison.OrdinalIgnoreCase)) return;

        var codeText = FindValue(values, "CODIGO_SAQUE")
                       ?? FindValue(values, "CODIGO_ATRASADO")
                       ?? FindValue(values, "CODIGO");
        var codeMatch = Regex.Match(codeText ?? string.Empty, @"A\d{2}", RegexOptions.IgnoreCase);
        var code = codeMatch.Success ? codeMatch.Value.ToUpperInvariant() : string.Empty;
        if (string.IsNullOrWhiteSpace(code)) return;

        var multiplier = code switch
        {
            "A58" or "A48" => 1m,
            "A53" or "A43" => 5m,
            "A52" or "A42" => 10m,
            _ => 0m
        };
        if (multiplier <= 0m) return;

        var daysText = FindValue(values, "QTD_DIAS")
                       ?? FindValue(values, "QUANTIDADE_DIAS")
                       ?? FindValue(values, "DIAS");
        if (!TryParseDecimalLoose(daysText, out var days) || days <= 0m) return;

        var etapaText = FindValue(values, "VALOR_ETAPA_COMUM")
                        ?? FindValue(values, "ETAPA_COMUM")
                        ?? "13,50";
        if (!TryParseDecimalLoose(etapaText, out var etapa) || etapa <= 0m) etapa = 13.50m;

        var valuePerDay = Math.Round(etapa * multiplier, 2, MidpointRounding.AwayFromZero);
        var total = Math.Round(valuePerDay * days, 2, MidpointRounding.AwayFromZero);
        // Estes campos são exclusivamente calculados. Sobrescrever valores antigos evita
        // que um preenchimento manual salvo produza total incorreto após mudar código/dias.
        values["VALOR_DIA"] = valuePerDay.ToString("N2", PtBr);
        values["VALOR_REFERENCIA_DIA"] = valuePerDay.ToString("N2", PtBr);
        values["VALOR_TOTAL"] = total.ToString("N2", PtBr);
        values["VALOR_TOTAL_INDIVIDUAL"] = total.ToString("N2", PtBr);
        values["MULTIPLICADOR_ETAPA"] = multiplier.ToString("0", CultureInfo.InvariantCulture);
    }

    private static bool TryParseDecimalLoose(string? text, out decimal value)
    {
        value = 0m;
        var clean = Regex.Replace(text ?? string.Empty, @"[^0-9,.-]", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(clean)) return false;
        if (decimal.TryParse(clean, NumberStyles.Number, PtBr, out value)) return true;
        if (decimal.TryParse(clean.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return true;
        return false;
    }

    public static string NormalizeTemplateName(string? value)
    {
        var text = CleanSpaces(value);
        if (string.IsNullOrWhiteSpace(text)) return "MODELO - Geral";
        text = Regex.Replace(text, @"\s*[–—]\s*", " - ");
        text = Regex.Replace(text, @"\s+-\s+", " - ");
        var match = Regex.Match(text, @"^(?<left>.+?)\s+-\s+(?<right>.+)$");
        if (!match.Success)
            return $"{text.ToUpper(PtBr)} - Geral";

        var left = CleanSpaces(match.Groups["left"].Value).ToUpper(PtBr);
        var right = SentenceCase(CleanSpaces(match.Groups["right"].Value));
        return string.IsNullOrWhiteSpace(right) ? $"{left} - Geral" : $"{left} - {right}";
    }

    private static Dictionary<string, string> NormalizeTemplateMap(Dictionary<string, string> source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            var name = NormalizeTemplateName(pair.Key);
            if (!string.IsNullOrWhiteSpace(name)) result[name] = pair.Value ?? string.Empty;
        }
        return result;
    }
    private static void RemoveModuleOwnedTemplateNames(Dictionary<string, string> source)
    {
        foreach (var key in source.Keys.Where(IsModuleOwnedTemplateName).ToList())
            source.Remove(key);
    }

    private static void EnsurePaymentTransferCodomFields(Dictionary<string, string> templates)
    {
        foreach (var name in templates.Keys.ToList())
        {
            var normalized = NormalizeKey(name);
            var text = templates[name] ?? string.Empty;
            if (!normalized.Contains("PAGAMENTO", StringComparison.OrdinalIgnoreCase)
                || !normalized.Contains("TRANSFERENCIA", StringComparison.OrdinalIgnoreCase)) continue;

            if (normalized.Contains("OUTRAOM", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("CODOM_DESTINO", StringComparison.OrdinalIgnoreCase))
                text = text.Replace("[[OM_DESTINO]]", "[[OM_DESTINO]], CODOM [[CODOM_DESTINO]]", StringComparison.OrdinalIgnoreCase);
            if (normalized.Contains("ESTAOM", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("CODOM_ORIGEM", StringComparison.OrdinalIgnoreCase))
                text = text.Replace("[[OM_ORIGEM]]", "[[OM_ORIGEM]], CODOM [[CODOM_ORIGEM]]", StringComparison.OrdinalIgnoreCase);
            templates[name] = text;
        }
    }

    private static void RemoveDeprecatedPaymentTransferTemplates(Dictionary<string, string> source)
    {
        foreach (var key in source.Keys.Where(name =>
                 NormalizeKey(name).Equals("PAGAMENTOTRANSFERENCIAPARAOUTRAOM", StringComparison.OrdinalIgnoreCase)
                 || NormalizeKey(name).Equals("PAGAMENTOTRANSFERENCIAPARAESTAOM", StringComparison.OrdinalIgnoreCase)).ToList())
            source.Remove(key);
    }

    private static bool IsModuleOwnedTemplateName(string? name)
    {
        var normalized = NormalizeKey(NormalizeTemplateName(name));
        return normalized.StartsWith("AUXILIOTRANSPORTE", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("GRATIFICACAODEREPRESENTACAO", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("GRATIFICACAOREPRESENTACAO", StringComparison.OrdinalIgnoreCase)
            || (normalized.StartsWith("AUXILIOALIMENTACAO", StringComparison.OrdinalIgnoreCase)
                && (normalized.Contains("CBESDFERIAS", StringComparison.OrdinalIgnoreCase)
                    || normalized.EndsWith("SAQUEDEATRASADO", StringComparison.OrdinalIgnoreCase)))
            || normalized.StartsWith("FERIAS", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ADICIONALFERIAS", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ADICIONALDEFERIAS", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("INDENIZACAODEFERIAS", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ANTECIPACAODAPRIMEIRAPARCELADOADICIONALNATALINOPARAFERIAS", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("ANTECIPACAO1PARCELAADICIONALNATALINO", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("AJUSTEDECONTAS", StringComparison.OrdinalIgnoreCase);
    }


    private static void NormalizePreferenceTemplateNames(BulletinPreferences preferences)
    {
        if (!string.IsNullOrWhiteSpace(preferences.LastTemplate))
            preferences.LastTemplate = NormalizeTemplateName(preferences.LastTemplate);

        preferences.TemplateOrder = preferences.TemplateOrder
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeTemplateName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        preferences.FormValues = MergeByNormalizedTemplate(preferences.FormValues);
        preferences.SelectionByTemplate = MergeByNormalizedTemplate(preferences.SelectionByTemplate);
    }

    private static Dictionary<string, Dictionary<string, string>> MergeByNormalizedTemplate(Dictionary<string, Dictionary<string, string>> source)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            var name = NormalizeTemplateName(pair.Key);
            if (!result.TryGetValue(name, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[name] = values;
            }
            foreach (var field in pair.Value)
                values[field.Key] = field.Value;
        }
        return result;
    }

    private static Dictionary<string, List<int>> MergeByNormalizedTemplate(Dictionary<string, List<int>> source)
    {
        var result = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            var name = NormalizeTemplateName(pair.Key);
            if (!result.TryGetValue(name, out var ids))
            {
                ids = [];
                result[name] = ids;
            }
            foreach (var id in pair.Value)
                if (!ids.Contains(id)) ids.Add(id);
        }
        return result;
    }

    private static string SentenceCase(string value)
    {
        var lower = CleanSpaces(value).ToLower(PtBr);
        if (lower.Length == 0) return "Geral";
        var chars = lower.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (!char.IsLetter(chars[index])) continue;
            chars[index] = char.ToUpper(chars[index], PtBr);
            break;
        }
        return new string(chars);
    }

    public static string FormatSmartValue(string? value, string type, string? format)
    {
        var field = new BulletinFieldDefinition
        {
            Key = "VALOR",
            Type = (type ?? string.Empty).Trim().ToLowerInvariant(),
            DateFormat = format ?? string.Empty,
            MoneyFormat = format ?? string.Empty,
            MonthFormat = format ?? string.Empty
        };
        return FormatValue(value ?? string.Empty, field);
    }

    private static void AddAutomaticDateKeys(IDictionary<string, string> result, DateTime date)
    {
        var rawDate = date.ToString("dd/MM/yyyy", PtBr);
        var rawMonth = date.ToString("MM/yyyy", PtBr);
        result["DATA_HOJE"] = rawDate;
        result["DATA_ATUAL"] = rawDate;
        result["DATA_HOJE_BR"] = rawDate;
        result["DATA_HOJE_ABREV"] = FormatDate(rawDate, "ABREV");
        result["DATA_HOJE_EXTENSO"] = FormatDate(rawDate, "EXTENSO");
        result["DIA_ATUAL"] = date.Day.ToString("00", PtBr);
        result["MES_ATUAL"] = rawMonth;
        result["MES_ATUAL_NOME"] = FormatMonth(rawMonth, "MES");
        result["MES_ATUAL_ABREV"] = FormatMonth(rawMonth, "ABREV");
        result["MES_ATUAL_NOME_ANO"] = FormatMonth(rawMonth, "MES_ANO");
        result["MES_ATUAL_ABREV_ANO"] = FormatMonth(rawMonth, "ABREV_ANO");
        result["MES_ATUAL_NUMERO"] = rawMonth;
        result["ANO_ATUAL"] = date.Year.ToString(PtBr);
    }

    public List<BulletinFieldDefinition> DetectFields(string text, bool includeAutomatic = false)
    {
        var found = new List<BulletinFieldDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TokenRegex().Matches(text ?? string.Empty))
        {
            var raw = FirstGroup(match);
            if (string.IsNullOrWhiteSpace(raw) || raw.Equals("ITEM", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("/ITEM", StringComparison.OrdinalIgnoreCase)) continue;
            var parsed = ParseField(raw);
            if ((!includeAutomatic && AutoFieldKeys.Contains(NormalizeKey(parsed.Key))) || !seen.Add(NormalizeKey(parsed.Key))) continue;
            found.Add(parsed);
        }
        return found;
    }

    public BulletinRenderResult Render(
        BulletinTemplate template,
        IReadOnlyList<MilitaryRecord> selected,
        IReadOnlyDictionary<string, string> formValues,
        IReadOnlyDictionary<string, string> globalKeys,
        IReadOnlyDictionary<int, Dictionary<string, string>>? perMilitaryKeys = null)
    {
        var effectiveFormValues = EnrichFormValues(template.Name, formValues);
        var common = globalKeys
            .Where(pair => IsMeaningfulFieldValue(pair.Key, pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in effectiveFormValues)
            if (IsMeaningfulFieldValue(pair.Key, pair.Value)) common[pair.Key] = pair.Value;

        var source = template.Text ?? string.Empty;
        if (selected.Count == 1)
        {
            if (perMilitaryKeys?.TryGetValue(selected[0].Id, out var documentKeys) == true)
                foreach (var pair in documentKeys) common[pair.Key] = pair.Value;
            foreach (var pair in MilitaryValues(selected[0])) common[pair.Key] = pair.Value;
        }

        var itemMatch = ItemBlockRegex().Match(source);
        string rendered;
        if (itemMatch.Success)
        {
            var before = source[..itemMatch.Index];
            var itemTemplate = itemMatch.Groups[1].Value;
            var after = source[(itemMatch.Index + itemMatch.Length)..];
            var items = new List<string>();
            foreach (var military in selected)
            {
                var values = new Dictionary<string, string>(common, StringComparer.OrdinalIgnoreCase);
                if (perMilitaryKeys?.TryGetValue(military.Id, out var documentKeys) == true)
                    foreach (var pair in documentKeys) values[pair.Key] = pair.Value;
                foreach (var pair in MilitaryValues(military)) values[pair.Key] = pair.Value;
                items.Add(ReplaceTokens(itemTemplate, values, military).Trim());
            }
            var separator = IsInlineBlock(before, after) ? ", " : Environment.NewLine + Environment.NewLine;
            rendered = ReplaceTokens(before, common, selected.Count == 1 ? selected[0] : null)
                       + string.Join(separator, items)
                       + ReplaceTokens(after, common, selected.Count == 1 ? selected[0] : null);
        }
        else
        {
            rendered = ReplaceTokens(source, common, selected.Count == 1 ? selected[0] : null).TrimEnd();
            if (selected.Count > 0)
            {
                var blocks = selected.Select(m =>
                {
                    var v = MilitaryValues(m);
                    return $"{v["POSTO_ABREV"]} {v["NOME"]}{Environment.NewLine}Prec-CP {v["PREC_CP"]} CPF {v["CPF"]}".Trim();
                });
                rendered += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, blocks);
            }
        }

        rendered = NormalizeLineEndings(rendered).Trim();
        return new BulletinRenderResult
        {
            Text = rendered,
            BoldRanges = FindWarNameRanges(rendered, selected),
            UnresolvedTokens = TokenRegex().Matches(rendered).Cast<Match>().Select(FirstGroup)
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.Equals("ITEM", StringComparison.OrdinalIgnoreCase) && !x.Equals("/ITEM", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    public FlowDocument BuildDocument(BulletinRenderResult result)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily(BulletinTextFormatter.StandardFontFamily),
            // Padrão visual das notas do SisBol/BI: Times New Roman 10 pt.
            FontSize = BulletinTextFormatter.StandardWpfFontSize,
            FontWeight = System.Windows.FontWeights.Normal,
            PagePadding = new System.Windows.Thickness(24),
            LineHeight = double.NaN
        };
        var paragraph = new Paragraph { Margin = new System.Windows.Thickness(0), TextAlignment = System.Windows.TextAlignment.Justify, FontWeight = System.Windows.FontWeights.Normal };
        var ranges = MergeRanges(result.BoldRanges, result.Text.Length);
        var cursor = 0;
        foreach (var range in ranges)
        {
            AddTextWithLineBreaks(paragraph, result.Text[cursor..range.Start], false);
            AddTextWithLineBreaks(paragraph, result.Text.Substring(range.Start, range.Length), true);
            cursor = range.Start + range.Length;
            if (cursor >= result.Text.Length || result.Text[cursor] is '\r' or '\n')
                paragraph.Inlines.Add(new Run("  ") { FontWeight = System.Windows.FontWeights.Normal });
        }
        if (cursor < result.Text.Length) AddTextWithLineBreaks(paragraph, result.Text[cursor..], false);
        document.Blocks.Add(paragraph);
        return document;
    }

    public static void CopyForWord(FlowDocument document, string plainText)
    {
        var range = new TextRange(document.ContentStart, document.ContentEnd);
        using var rtf = new MemoryStream();
        range.Save(rtf, System.Windows.DataFormats.Rtf);
        rtf.Position = 0;
        using var reader = new StreamReader(rtf, Encoding.ASCII, true, 1024, leaveOpen: true);
        var data = new System.Windows.DataObject();
        data.SetData(System.Windows.DataFormats.UnicodeText, plainText);
        data.SetData(System.Windows.DataFormats.Text, plainText);
        data.SetData(System.Windows.DataFormats.Rtf, reader.ReadToEnd());
        System.Windows.Clipboard.SetDataObject(data, true);
    }

    private string ReplaceTokens(string text, IReadOnlyDictionary<string, string> values, MilitaryRecord? military)
    {
        return TokenRegex().Replace(text ?? string.Empty, match =>
        {
            var raw = FirstGroup(match);
            var parsed = ParseField(raw);
            var key = parsed.Key;
            if (NormalizeKey(key).Equals("TEMPOSERVICO", StringComparison.OrdinalIgnoreCase) && military is not null)
                return military.ServiceTimeText;
            var value = FindValue(values, key);
            if (value is null) return match.Value;
            var formatted = FormatValue(value, parsed);
            return IsPublicationReferenceKey(key) ? RemoveYearFromPublicationReference(formatted) : formatted;
        });
    }

    private static bool IsPublicationReferenceKey(string? key)
    {
        var normalized = NormalizeKey(key);
        return normalized.Contains("BIREFERENCIA", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("REFERENCIABOLETIM", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("PUBLICACAOBI", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("BOLETIMREFERENCIA", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("BINUMERO", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("NUMBI", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("ADTREFERENCIA", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("NUMADT", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveYearFromPublicationReference(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        return Regex.Replace(
            text,
            @"\b(?<num>\d{1,5})\s*/\s*(?:20)?\d{2}\b",
            match => match.Groups["num"].Value,
            RegexOptions.CultureInvariant);
    }

    private static string? FindValue(IEnumerable<KeyValuePair<string, string>> values, string key)
    {
        var normalized = NormalizeKey(key);
        foreach (var pair in values)
            if (NormalizeKey(pair.Key).Equals(normalized, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        return null;
    }

    private static Dictionary<string, string> MilitaryValues(MilitaryRecord military)
    {
        var name = NameHighlightHelper.PlainDisplay((military.Name ?? string.Empty).ToUpperInvariant(), military.WarName);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["POSTO"] = military.Rank ?? string.Empty,
            ["POSTO_ABREV"] = MilitaryRankService.ShortName(military.Rank),
            ["NOME"] = name,
            ["NOME_COMPLETO"] = (military.Name ?? string.Empty).ToUpperInvariant(),
            ["NOME_DESTACADO"] = name,
            ["NOME_GUERRA"] = military.WarName ?? string.Empty,
            ["CPF"] = MilitaryFormatting.FormatCpf(military.Cpf),
            ["PREC_CP"] = military.PrecCp ?? string.Empty,
            ["IDT"] = military.MilitaryId ?? string.Empty,
            ["EMAIL"] = military.Email ?? string.Empty,
            ["ESCOLARIDADE"] = military.Education ?? string.Empty,
            ["TELEFONE"] = military.Phone ?? string.Empty,
            ["ENDERECO"] = military.Address ?? string.Empty,
            ["CEP"] = military.ZipCode ?? string.Empty,
            ["BANCO"] = military.Bank ?? string.Empty,
            ["AGENCIA"] = military.Agency ?? string.Empty,
            ["CONTA"] = military.Account ?? string.Empty,
            ["TEMPO_SERVICO"] = military.ServiceTimeText,
            ["MILITAR_FORMATADO"] = $"{MilitaryRankService.ShortName(military.Rank)} {name}{Environment.NewLine}Prec-CP {military.PrecCp} CPF {MilitaryFormatting.FormatCpf(military.Cpf)}".Trim()
        };
    }

    private static BulletinFieldDefinition ParseField(string raw)
    {
        var value = Regex.Replace(raw ?? string.Empty, @"\s+", " ").Trim();
        var keyPart = value;
        var meta = string.Empty;
        var colon = value.IndexOf(':');
        if (colon >= 0) { keyPart = value[..colon].Trim(); meta = value[(colon + 1)..].Trim(); }

        var displayKey = keyPart;
        var formKey = keyPart;
        foreach (var separator in new[] { "->", "=>" })
        {
            var pos = keyPart.IndexOf(separator, StringComparison.Ordinal);
            if (pos > 0) { displayKey = keyPart[..pos].Trim(); formKey = keyPart[(pos + separator.Length)..].Trim(); break; }
        }
        if (formKey == keyPart)
        {
            var suffix = keyPart.IndexOfAny(['#', '@']);
            if (suffix > 0) formKey = keyPart[..suffix].Trim();
        }

        var field = new BulletinFieldDefinition { Key = formKey, DisplayKey = displayKey, Meta = meta };
        var normalizedMeta = NormalizeKey(meta);
        var metaUpper = RemoveAccents(meta).ToUpperInvariant();
        if (normalizedMeta == "DATA" || metaUpper.StartsWith("DATA="))
        {
            field.Type = "date";
            if (meta.Contains('=')) field.DateFormat = meta[(meta.IndexOf('=') + 1)..].Trim();
        }
        else if (normalizedMeta == "VALOR" || metaUpper.StartsWith("VALOR="))
        {
            field.Type = "money";
            if (meta.Contains('=')) field.MoneyFormat = meta[(meta.IndexOf('=') + 1)..].Trim();
        }
        else if (normalizedMeta == "MES" || metaUpper.StartsWith("MES="))
        {
            field.Type = "month";
            if (meta.Contains('=')) field.MonthFormat = meta[(meta.IndexOf('=') + 1)..].Trim();
        }
        else if (metaUpper.StartsWith("LISTA="))
        {
            field.Type = "select";
            field.Options = meta[(meta.IndexOf('=') + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        return field;
    }

    private static string FormatValue(string value, BulletinFieldDefinition field)
    {
        return field.Type switch
        {
            "date" => FormatDate(value, field.DateFormat),
            "money" => FormatMoney(value, field.MoneyFormat),
            "month" => FormatMonth(value, field.MonthFormat),
            _ => value
        };
    }

    private static string FormatDate(string value, string format)
    {
        if (!TryParseDate(value, out var date)) return value;
        var f = NormalizeKey(format);
        return f switch
        {
            "ABREV" or "ABREVIADO" or "DDMMMAA" or "DDMONYY" => date.ToString("dd MMM yy", PtBr).Replace(".", string.Empty).ToUpperInvariant(),
            "EXTENSO" or "POREXTENSO" or "DDEMESEAAAA" => $"{date.Day} de {date.ToString("MMMM", PtBr)} de {date.Year}",
            "ISO" => date.ToString("yyyy-MM-dd", PtBr),
            _ => date.ToString("dd/MM/yyyy", PtBr)
        };
    }

    private static string FormatMonth(string value, string format)
    {
        if (!TryParseMonth(value, out var date)) return value;
        var f = NormalizeKey(format);
        var month = date.ToString("MMMM", PtBr);
        var monthTitle = PtBr.TextInfo.ToTitleCase(month);
        var abbreviation = date.ToString("MMM", PtBr).Replace(".", string.Empty).ToUpperInvariant();
        var days = DateTime.DaysInMonth(date.Year, date.Month);
        return f switch
        {
            "ABREV" or "SIGLA" or "ABREVIADO" => abbreviation,
            "ANO" => date.Year.ToString(PtBr),
            "ABREVANO" => $"{abbreviation} {date:yy}",
            "MESANO" or "NOMEANO" => $"{monthTitle} {date.Year}",
            "NUMERO" => date.ToString("MM/yyyy", PtBr),
            "DIAS" or "QTDDIAS" => $"{days} dias",
            "DIASMES" => $"{days} dias do mês de {month}",
            "DIASABREV" => $"{days} dias do mês de {abbreviation}",
            "DIASMESANO" => $"{days} dias do mês de {month} de {date.Year}",
            "DIASABREVANO" => $"{days} dias do mês de {abbreviation} {date.Year}",
            _ => monthTitle
        };
    }

    private static string FormatMoney(string value, string format)
    {
        if (!NumberToWordsService.TryParse(value, out var number)) return value;
        var numeric = NumberToWordsService.FormatNumber(number, true);
        var words = NumberToWordsService.Convert(number, true);
        var f = NormalizeKey(format);
        return f switch
        {
            "EXTENSO" or "POREXTENSO" => words,
            "EXTENSOMAIUSCULO" or "MAIUSCULO" => words.ToUpperInvariant(),
            "AMBOS" or "NUMEROEXTENSO" or "JUNTO" => $"{numeric} ({words})",
            "AMBOSMAIUSCULO" => $"{numeric} ({words.ToUpperInvariant()})",
            _ => numeric
        };
    }

    private static bool TryParseDate(string value, out DateTime date)
    {
        foreach (var format in new[] { "dd/MM/yyyy", "dd/MM/yy", "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy", "dd MMM yy", "d 'de' MMMM 'de' yyyy" })
            if (DateTime.TryParseExact(value?.Trim(), format, PtBr, DateTimeStyles.AllowWhiteSpaces, out date)) return true;
        return DateTime.TryParse(value, PtBr, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static bool TryParseMonth(string value, out DateTime date)
    {
        foreach (var format in new[] { "MM/yyyy", "M/yyyy", "yyyy-MM", "MMM/yy", "MMM yy", "MMM yyyy", "MMMM/yyyy", "MMMM yyyy", "MMMM 'de' yyyy" })
            if (DateTime.TryParseExact(value?.Trim(), format, PtBr, DateTimeStyles.AllowWhiteSpaces, out date)) return true;
        return DateTime.TryParse(value, PtBr, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static List<BulletinBoldRange> FindWarNameRanges(string text, IReadOnlyList<MilitaryRecord> selected)
        => BulletinTextFormatter.FindWarNameRanges(text, selected);

    private static List<BulletinBoldRange> MergeRanges(IEnumerable<BulletinBoldRange> source, int textLength)
    {
        var ordered = source.Where(x => x.Start >= 0 && x.Length > 0 && x.Start < textLength)
            .Select(x => new BulletinBoldRange(x.Start, Math.Min(x.Length, textLength - x.Start)))
            .OrderBy(x => x.Start).ThenBy(x => x.Length).ToList();
        var result = new List<BulletinBoldRange>();
        foreach (var item in ordered)
        {
            if (result.Count == 0 || item.Start > result[^1].Start + result[^1].Length) { result.Add(item); continue; }
            var previous = result[^1];
            var end = Math.Max(previous.Start + previous.Length, item.Start + item.Length);
            result[^1] = new BulletinBoldRange(previous.Start, end - previous.Start);
        }
        return result;
    }

    private static void AddTextWithLineBreaks(Paragraph paragraph, string text, bool bold)
    {
        var parts = NormalizeLineEndings(text).Split('\n');
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length > 0) paragraph.Inlines.Add(new Run(parts[index]) { FontWeight = bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal });
            if (index < parts.Length - 1) paragraph.Inlines.Add(new LineBreak());
        }
    }

    private async Task<Dictionary<string, string>> ReadTemplateMapAsync(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var map = await _json.LoadAsync<Dictionary<string, string>>(path) ?? [];
            return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"Falha ao carregar modelos de boletim: {path}", ex);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Renumber(IReadOnlyList<BulletinTemplate> templates)
    {
        for (var index = 0; index < templates.Count; index++) templates[index].Order = index;
    }

    private static string FirstGroup(Match match)
        => Enumerable.Range(1, match.Groups.Count - 1).Select(i => match.Groups[i].Value).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;

    private static string NormalizeKey(string? value)
        => new string(RemoveAccents(value).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string CleanSpaces(string? value)
        => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string RemoveAccents(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    private static string NormalizeLineEndings(string value) => (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    private static bool IsInlineBlock(string before, string after) => !before.EndsWith('\n') && !after.StartsWith('\n');
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    [GeneratedRegex(@"\{\{([^}]+)\}\}|____([^_]+)____|\[\[([^\]]+)\]\]")]
    private static partial Regex TokenRegex();
    [GeneratedRegex(@"\[\[ITEM\]\](.*?)\[\[/ITEM\]\]", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ItemBlockRegex();
}
