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
        return catalog.Rules
            .Select(rule => new { Rule = rule, Score = MatchScore(rule, query) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Rule.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.Rule)
            .FirstOrDefault();
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
        BulletinRenderResult render)
    {
        var report = new BulletinComplianceReport { Rule = rule };
        if (rule is null)
        {
            report.Score = 70;
            report.Warnings.Add("Este modelo ainda não possui regra SIPPES vinculada. Revise manualmente todos os dados antes do envio.");
            return report;
        }

        foreach (var field in rule.RequiredFields)
        {
            var issue = ValidateField(field, template, military, formValues, globalValues);
            if (string.IsNullOrWhiteSpace(issue))
            {
                report.Passed.Add(field.Label);
                continue;
            }
            report.Warnings.Add(issue);
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
            report.Warnings.Add(message);
        }

        foreach (var token in render.UnresolvedTokens)
            report.Errors.Add($"Campo pendente no texto: {token}.");
        report.Recommendations.AddRange(rule.Checklist);
        report.Recommendations.AddRange(rule.RelatedPublications.Select(item => "Publicação relacionada: " + item));
        report.Errors = report.Errors.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        report.Warnings = report.Warnings.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        report.Passed = report.Passed.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        report.Score = Math.Clamp(100 - report.Errors.Count * 18 - report.Warnings.Count * 5, 0, 100);
        return report;
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
            if (!ContainsToken(template.Text, field.Key))
                return $"Sugestão não presente no modelo: {field.Label} ([[{field.Key}]]).";
            if (military.Count == 0) return $"Adicione militar(es) para preencher {field.Label}.";
            var missing = military.Where(item => string.IsNullOrWhiteSpace(MilitaryValue(item, field.Key)))
                .Select(item => string.IsNullOrWhiteSpace(item.WarName) ? item.Name : item.WarName).ToList();
            return missing.Count == 0 ? null : $"{field.Label} ausente para: {string.Join(", ", missing)}.";
        }

        if (!ContainsToken(template.Text, field.Key))
            return $"Sugestão não presente no modelo: {field.Label} ([[{field.Key}]]).";
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
