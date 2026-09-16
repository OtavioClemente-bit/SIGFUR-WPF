using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class BulletinKnowledgeService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BulletinKnowledgeCatalog? _cache;

    public BulletinKnowledgeService(AppPaths paths, JsonFileService json, LogService log)
    {
        _paths = paths;
        _json = json;
        _log = log;
    }

    public async Task<BulletinKnowledgeCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null) return _cache;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null) return _cache;
            _cache = await _json.LoadAsync<BulletinKnowledgeCatalog>(_paths.BulletinKnowledgeFile)
                     ?? new BulletinKnowledgeCatalog();
            return _cache;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao carregar a base de conhecimento dos boletins.", ex);
            return new BulletinKnowledgeCatalog();
        }
        finally { _gate.Release(); }
    }

    public async Task<BulletinKnowledgeRule?> FindRuleAsync(string? templateName, CancellationToken cancellationToken = default)
    {
        var query = Normalize(templateName);
        if (string.IsNullOrWhiteSpace(query)) return null;
        var catalog = await LoadAsync(cancellationToken);
        return ResolveTemplateRule(catalog.Rules, templateName);
    }

    public static BulletinKnowledgeRule? ResolveTemplateRule(IEnumerable<BulletinKnowledgeRule> rules, string? name)
    {
        var query = Normalize(name);
        var candidates = rules.ToList();
        var exact = candidates.Where(rule => Normalize(rule.TemplateName) == query || Normalize(rule.Title) == query).ToList();
        if (exact.Count == 1) return exact[0];
        // Search relevance is not a safe rule binding: inclusion and exclusion share keywords.
        var aliases = candidates.Where(rule => rule.Aliases.Any(alias => Normalize(alias) == query)).ToList();
        return aliases.Count == 1 ? aliases[0] : null;
    }

    public async Task<List<BulletinKnowledgeRule>> SearchAsync(string? query, int limit = 8, CancellationToken cancellationToken = default)
    {
        var catalog = await LoadAsync(cancellationToken);
        var normalized = Normalize(query);
        if (string.IsNullOrWhiteSpace(normalized))
            return catalog.Rules.OrderBy(rule => rule.Category).ThenBy(rule => rule.Title).Take(limit).ToList();
        return catalog.Rules
            .Select(rule => new { Rule = rule, Score = MatchScore(rule, normalized) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Rule.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Clamp(limit, 1, 20))
            .Select(item => item.Rule)
            .ToList();
    }

    public BulletinComplianceReport Validate(
        BulletinKnowledgeRule? rule,
        BulletinTemplate template,
        IReadOnlyList<MilitaryRecord> military,
        IReadOnlyDictionary<string, string> formValues,
        IReadOnlyDictionary<string, string> globalValues,
        BulletinRenderResult render,
        IReadOnlyDictionary<int, Dictionary<string, string>>? perMilitaryValues = null)
    {
        var report = new BulletinComplianceReport { Rule = rule };
        foreach (var token in render.UnresolvedTokens)
            report.Errors.Add($"Campo pendente no texto: {token}.");
        if (military.Count == 0)
            report.Errors.Add("Selecione ao menos um militar para gerar a publicação.");
        if (string.IsNullOrWhiteSpace(render.Text))
            report.Errors.Add("O texto da publicação está vazio.");
        ValidateInputValues(template, formValues, report);
        if (perMilitaryValues is not null)
            foreach (var person in military)
                if (perMilitaryValues.TryGetValue(person.Id, out var personalValues))
                    ValidateInputValues(template, personalValues, report, person.DisplayName + ": ");
        if (rule is null)
        {
            report.Score = 70;
            report.Warnings.Add("Este modelo ainda não possui regra SIPPES vinculada. Revise manualmente todos os dados antes do envio.");
            return report;
        }

        foreach (var field in rule.RequiredFields)
        {
            // A base de conhecimento é apenas uma referência. Se a chave foi retirada
            // do modelo pelo usuário, valores antigos salvos no formulário não podem
            // continuar aparecendo como pendência nem bloquear o envio.
            if (!ContainsToken(template.Text, field.Key)) continue;
            var issue = ValidateField(field, template, military, formValues, globalValues);
            if (!field.Source.Equals("military", StringComparison.OrdinalIgnoreCase))
            {
                // The rendered text already accounts for individual values, calculations and
                // conditional lines. A removed/dispensed token must not become a ghost requirement.
                var unresolved = render.UnresolvedTokens.Any(token => Normalize(token.Split(':', '#')[0]) == Normalize(field.Key));
                if (!unresolved) issue = null;
            }
            if (string.IsNullOrWhiteSpace(issue))
            {
                report.Passed.Add(field.Label);
                continue;
            }
            AddIssue(report, field.Severity, issue);
        }

        var normalizedText = Normalize(render.Text);
        foreach (var requirement in rule.RequiredText)
        {
            if (requirement.AnyOf.Any(term => normalizedText.Contains(Normalize(term), StringComparison.Ordinal)))
            {
                report.Passed.Add(requirement.Label);
                continue;
            }
            var message = $"O texto não apresenta: {requirement.Label}.";
            // Requisitos narrativos da base são orientação de revisão. Só bloqueiam
            // quando a regra declarar explicitamente severidade "error".
            AddIssue(report, string.IsNullOrWhiteSpace(requirement.Severity) ? "warning" : requirement.Severity, message);
        }

        ValidateConditionalFields(rule, template, formValues, globalValues, report);

        if (perMilitaryValues is not null)
        {
            foreach (var person in military)
            {
                if (!perMilitaryValues.TryGetValue(person.Id, out var individual)) continue;
                var effective = new Dictionary<string, string>(formValues, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in individual) effective[pair.Key] = pair.Value;
                var personalReport = new BulletinComplianceReport();
                ValidateConditionalFields(rule, template, effective, globalValues, personalReport);
                foreach (var error in personalReport.Errors)
                    report.Errors.Add($"{person.DisplayName}: {error}");
            }
        }

        report.Recommendations.AddRange(rule.Checklist);
        AddManualGuidance(rule, report);
        report.Recommendations.AddRange(rule.RelatedPublications.Select(item => "Publicação relacionada: " + item));
        report.Errors = report.Errors.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        report.Warnings = report.Warnings.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        report.Passed = report.Passed.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        report.Score = Math.Clamp(100 - report.Errors.Count * 18 - report.Warnings.Count * 5, 0, 100);
        return report;
    }

    private static void ValidateInputValues(BulletinTemplate template, IReadOnlyDictionary<string, string> values, BulletinComplianceReport report, string prefix = "")
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        foreach (var pair in values)
        {
            if (!ContainsToken(template.Text, pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
            var key = Normalize(pair.Key);
            if (key.StartsWith("data ", StringComparison.Ordinal)
                && !DateTime.TryParse(pair.Value, culture, DateTimeStyles.AllowWhiteSpaces, out _))
                report.Errors.Add($"{prefix}{pair.Key}: informe uma data válida (dia/mês/ano).");
            if ((key.StartsWith("qtd ", StringComparison.Ordinal) || key.StartsWith("quantidade ", StringComparison.Ordinal))
                && (!int.TryParse(pair.Value, out var quantity) || quantity < 0))
                report.Errors.Add($"{prefix}{pair.Key}: informe uma quantidade inteira, igual ou maior que zero.");
        }
        foreach (var (start, end) in new[] { ("DATA_INICIO", "DATA_FIM"), ("DATA_INICIAL", "DATA_FINAL") })
        {
            if (!ContainsToken(template.Text, start) || !ContainsToken(template.Text, end)) continue;
            if (DateTime.TryParse(FindValue(values, start), culture, DateTimeStyles.None, out var from)
                && DateTime.TryParse(FindValue(values, end), culture, DateTimeStyles.None, out var to) && to < from)
                report.Errors.Add($"{prefix}A data final não pode ser anterior à data inicial.");
        }
    }

    private static void AddManualGuidance(BulletinKnowledgeRule rule, BulletinComplianceReport report)
    {
        var name = Normalize(rule.TemplateName);
        const string source = "Manual Técnico SIPPES, 17/07/2026";
        if (name.Contains("auxilio transporte", StringComparison.Ordinal))
        {
            report.Recommendations.Add($"{source}, pp. 128–131: NR0095 normal; AR0095 atrasados; FR0095 diferença líquida; DR0095 devolução. Confira a natureza do lançamento antes de escolher a rubrica.");
            report.Recommendations.Add("Transporte: a competência de uso é o mês seguinte à folha de recebimento. Exemplo do manual (p. 130): ausência na folha de abril → referência maio na AR0095. Confira também a virada do ano.");
        }
        if (name.Contains("auxilio alimentacao", StringComparison.Ordinal))
            report.Recommendations.Add($"{source}, pp. 132–133: NR corresponde ao direito da folha vigente; AR, a direito anterior; FR, à diferença; DR, à devolução. Confira os dias e a modalidade de alimentação de cada militar.");
        if (name.Contains("pre escolar", StringComparison.Ordinal))
            report.Recommendations.Add($"{source}, p. 125: NR0077 é automática pelo vínculo do dependente; AR0077/FR0077/DR0077 exigem o dependente vinculado. Para atrasados de vários meses, faça um lançamento por mês.");
        if (name.Contains("pensao judicial", StringComparison.Ordinal))
            report.Recommendations.Add($"{source}, p. 84: a data inicial de vigência não gera automaticamente pagamento retroativo. Confira os atrasados separadamente e respeite a decisão judicial.");
    }

    private static void ValidateConditionalFields(
        BulletinKnowledgeRule rule,
        BulletinTemplate bulletinTemplate,
        IReadOnlyDictionary<string, string> formValues,
        IReadOnlyDictionary<string, string> globalValues,
        BulletinComplianceReport report)
    {
        var template = Normalize(rule.TemplateName);
        if (template.Contains("pensao", StringComparison.Ordinal)
            && ContainsToken(bulletinTemplate.Text, "BANCO_GUARDA")
            && (ContainsToken(bulletinTemplate.Text, "OPERACAO_CAIXA_OPCIONAL") || ContainsToken(bulletinTemplate.Text, "OPERACAO_CAIXA")))
        {
            var bank = FindValue(formValues, "BANCO_GUARDA") ?? FindValue(globalValues, "BANCO_GUARDA") ?? string.Empty;
            var isCaixa = bank.Contains("104", StringComparison.OrdinalIgnoreCase) || bank.Contains("Caixa", StringComparison.OrdinalIgnoreCase);
            var operation = FindValue(formValues, "OPERACAO_CAIXA_OPCIONAL")
                            ?? FindValue(formValues, "OPERACAO_CAIXA")
                            ?? FindValue(globalValues, "OPERACAO_CAIXA_OPCIONAL")
                            ?? FindValue(globalValues, "OPERACAO_CAIXA");
            if (isCaixa && string.IsNullOrWhiteSpace(operation))
                report.Errors.Add("Preencha operação CAIXA quando o domicílio bancário for Caixa Econômica Federal.");
        }

        foreach (var key in new[] { "RESUMO_DECISAO", "JUSTIFICATIVA_EXCLUSAO" })
        {
            if (!ContainsToken(bulletinTemplate.Text, key)) continue;
            var value = FindValue(formValues, key) ?? FindValue(globalValues, key);
            if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length > 250)
                report.Errors.Add($"{FriendlyFieldName(key)} deve ter no máximo 250 caracteres.");
        }
    }

    private static string FriendlyFieldName(string key) => Normalize(key) switch
    {
        "resumo decisao" => "Resumo da decisão",
        "justificativa exclusao" => "Justificativa da exclusão",
        _ => key
    };

    private static void AddIssue(BulletinComplianceReport report, string? severity, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (severity?.Equals("warning", StringComparison.OrdinalIgnoreCase) == true)
            report.Warnings.Add(message);
        else
            report.Errors.Add(message);
    }

    public async Task<string> BuildAssistantContextAsync(string? prompt, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(prompt);
        if (!Regex.IsMatch(normalized, @"\b(boletim|aditamento|sippes|direito|saque|implantacao|despesa|ferias|auxilio|pensao)\b"))
            return string.Empty;
        var matches = await SearchAsync(prompt, 3, cancellationToken);
        if (matches.Count == 0) return string.Empty;
        var text = new StringBuilder("BASE LOCAL DE CONHECIMENTO DE BOLETINS/SIPPES:\n");
        foreach (var rule in matches)
        {
            text.AppendLine($"- {rule.Title}: {rule.Summary}");
            text.AppendLine("  Itens sugeridos para conferência: " + string.Join(", ", rule.RequiredFields.Select(field => field.Label)) + ".");
            if (rule.RelatedPublications.Count > 0)
                text.AppendLine("  Dependências: " + string.Join(" | ", rule.RelatedPublications) + ".");
            if (rule.AiGuidance.Count > 0)
                text.AppendLine("  Regras: " + string.Join(" | ", rule.AiGuidance) + ".");
        }
        text.Append("Nunca invente valores ausentes; marque-os como [PREENCHER] e apresente-os como pontos de conferência.");
        return text.ToString();
    }

    private static string? ValidateField(
        BulletinKnowledgeField field,
        BulletinTemplate template,
        IReadOnlyList<MilitaryRecord> military,
        IReadOnlyDictionary<string, string> formValues,
        IReadOnlyDictionary<string, string> globalValues)
    {
        if (field.Source.Equals("military", StringComparison.OrdinalIgnoreCase))
        {
            if (military.Count == 0) return $"Adicione militar(es) para preencher {field.Label}.";
            var missing = military.Where(item => string.IsNullOrWhiteSpace(MilitaryValue(item, field.Key)))
                .Select(item => string.IsNullOrWhiteSpace(item.WarName) ? item.Name : item.WarName).ToList();
            return missing.Count == 0 ? null : $"{field.Label} ausente para: {string.Join(", ", missing)}.";
        }
        var value = FindValue(formValues, field.Key) ?? FindValue(globalValues, field.Key);
        return string.IsNullOrWhiteSpace(value) ? $"Preencha {field.Label}." : null;
    }

    private static string MilitaryValue(MilitaryRecord military, string key) => Normalize(key).Replace(" ", string.Empty, StringComparison.Ordinal) switch
    {
        "postograd" or "posto" or "postoabrev" => military.Rank,
        "nome" or "nomecompleto" or "nomeguerra" => key.Contains("GUERRA", StringComparison.OrdinalIgnoreCase) ? military.WarName : military.Name,
        "preccp" => military.PrecCp,
        "cpf" => military.Cpf,
        "idt" or "identidade" => military.MilitaryId,
        _ => string.Empty
    };

    private static string? FindValue(IReadOnlyDictionary<string, string> values, string key)
    {
        var normalized = Normalize(key);
        return values.FirstOrDefault(pair => Normalize(pair.Key) == normalized).Value;
    }

    private static bool ContainsToken(string text, string key)
        => Regex.IsMatch(text ?? string.Empty, $@"(?:\[\[|\{{\{{)\s*{Regex.Escape(key)}(?:[:#][^\]\}}]*)?(?:\]\]|\}}\}})", RegexOptions.IgnoreCase);

    private static int MatchScore(BulletinKnowledgeRule rule, string normalizedQuery)
    {
        var names = new[] { rule.Id, rule.TemplateName, rule.Title }.Concat(rule.Aliases).Select(Normalize).Where(value => value.Length > 0).ToList();
        if (names.Any(name => name == normalizedQuery)) return 100;
        if (names.Any(name => name.Contains(normalizedQuery, StringComparison.Ordinal) || normalizedQuery.Contains(name, StringComparison.Ordinal))) return 80;
        var terms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(term => term.Length > 2).Distinct().ToList();
        if (terms.Count == 0) return 0;
        var haystack = Normalize(rule.SearchText);
        var hits = terms.Count(term => haystack.Contains(term, StringComparison.Ordinal));
        return hits == 0 ? 0 : hits * 10 + (hits == terms.Count ? 20 : 0);
    }

    private static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }
}
