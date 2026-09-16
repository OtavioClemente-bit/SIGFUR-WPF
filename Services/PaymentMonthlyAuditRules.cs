using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class PaymentMonthlyAuditRules
{
    public static bool IsDeterministicallyClosed(PaymentConferenceResultRow row)
        => row.Origin == "PARSER_SIGFUR" && row.Status == "ACHOU RUBRICA" && row.Severity == "success"
           && (!row.HasExpectedAmount || row.HasPaidAmount && Math.Abs(row.Difference) <= .05);

    public static bool IsMissingPaystubReflection(PaymentConferenceResultRow row)
        => !string.IsNullOrWhiteSpace(row.BulletinPath)
           && row.Status is "NÃO ACHOU RUBRICA" or "CONTRACHEQUE NÃO ENCONTRADO";

    public static List<PaymentRubricChange> CompareRubricChanges(
        IReadOnlyList<PaymentConferenceRubricHit> current,
        IReadOnlyList<PaymentConferenceRubricHit> previous,
        IReadOnlyList<PaymentConferenceExpectedItem> publications,
        double tolerance = 0.05)
    {
        static Dictionary<string, PaymentConferenceRubricHit> Index(IEnumerable<PaymentConferenceRubricHit> rows)
            => rows.Where(x => x.Cpf.Length > 0 && x.Code.Length > 0)
                .GroupBy(x => Key(x.Cpf, x.Code), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new PaymentConferenceRubricHit
                {
                    Cpf = Digits(g.First().Cpf), Military = g.First().Military, Code = g.First().Code,
                    Description = g.First().Description, Value = g.Sum(x => x.Value), PaystubPath = g.First().PaystubPath
                }, StringComparer.Ordinal);

        var now = Index(current); var before = Index(previous);
        var changes = new List<PaymentRubricChange>();
        foreach (var key in now.Keys.Union(before.Keys, StringComparer.Ordinal).Order())
        {
            now.TryGetValue(key, out var currentRow); before.TryGetValue(key, out var previousRow);
            if (currentRow is not null && previousRow is not null && Math.Abs(currentRow.Value - previousRow.Value) <= tolerance)
                continue; // recorrente e inalterada: não exige publicação mensal.
            var row = currentRow ?? previousRow!;
            var hasPublication = publications.Any(item => Digits(item.Cpf) == Digits(row.Cpf)
                && item.ExpectedCodes.Contains(row.Code, StringComparer.OrdinalIgnoreCase));
            changes.Add(new PaymentRubricChange
            {
                Cpf = Digits(row.Cpf), Military = row.Military, Code = row.Code, Description = row.Description,
                ChangeType = currentRow is null ? "RUBRICA_REMOVIDA" : previousRow is null ? "RUBRICA_NOVA" : "VALOR_ALTERADO",
                PreviousValue = previousRow?.Value, CurrentValue = currentRow?.Value,
                CurrentPaystubPath = currentRow?.PaystubPath ?? string.Empty, HasBulletinEvidence = hasPublication
            });
        }
        return changes;
    }

    public static string? ValidateReturnedIds(IEnumerable<string> expectedIds, PaymentAiAuditResponse response)
    {
        var allowedStatuses = new HashSet<string>(
            ["COERENTE", "DIVERGENCIA", "SEM_REFLEXO_NO_CONTRACHEQUE", "SEM_PUBLICACAO_ENCONTRADA", "REVISÃO_NECESSÁRIA", "INCONCLUSIVO"],
            StringComparer.Ordinal);
        var expected = expectedIds.ToList();
        var returned = response.Results.Select(x => x.ItemId).ToList();
        var missing = expected.Except(returned, StringComparer.Ordinal).ToList();
        var unexpected = returned.Except(expected, StringComparer.Ordinal).ToList();
        var duplicates = returned.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        var invalidResults = response.Results.Where(item => !allowedStatuses.Contains(item.Status)
                                                             || string.IsNullOrWhiteSpace(item.Finding)
                                                             || item.Confidence is < 0 or > 1)
            .Select(item => item.ItemId).ToList();
        return missing.Count == 0 && unexpected.Count == 0 && duplicates.Count == 0 && invalidResults.Count == 0
            ? null
            : $"Resposta estruturada inválida. Ausentes: {string.Join(", ", missing)}; inesperados: {string.Join(", ", unexpected)}; duplicados: {string.Join(", ", duplicates)}; resultados inválidos: {string.Join(", ", invalidResults)}.";
    }

    private static string Key(string cpf, string code) => Digits(cpf) + "|" + code.Trim().ToUpperInvariant();
    private static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());
}
