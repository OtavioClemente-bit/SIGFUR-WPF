using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed partial class PaymentConferenceService
{
    public async Task<int> MergeAiRescueAsync(PaymentConferenceResult result, IEnumerable<AiRescuePublication> rescued,
        PaymentConferenceSettings settings, CancellationToken cancellationToken = default)
    {
        var candidates = rescued.Select(ToExpectedItem).ToList();
        var added = new List<PaymentConferenceExpectedItem>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duplicate = result.ExpectedItems.FirstOrDefault(existing => SamePublication(existing, candidate));
            if (duplicate is not null)
            {
                if (duplicate.ExpectedAmount > 0 && candidate.ExpectedAmount > 0
                    && Math.Abs(duplicate.ExpectedAmount - candidate.ExpectedAmount) > settings.Tolerance)
                    result.Warnings.Add($"Conflito Parser SIGFUR x IA Rescue em {candidate.Bulletin}, pág. {candidate.Page}, {candidate.Name}. Revisão humana necessária; o resultado determinístico foi preservado.");
                continue;
            }
            added.Add(candidate);
        }
        if (added.Count == 0) return 0;

        var military = await LoadConferenceMilitaryAsync(cancellationToken);
        var byCpf = military.Where(x => Digits(x.Cpf).Length == 11).GroupBy(x => Digits(x.Cpf))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byPrec = military.Where(x => Digits(x.PrecCp).Length >= 6).GroupBy(x => Digits(x.PrecCp).TrimStart('0'))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var scratch = new PaymentConferenceResult();
        var documents = await ReadConferencePdfsAsync(settings, scratch, null, cancellationToken);
        foreach (var item in added)
        {
            MatchMilitary(item, byCpf, byPrec, military);
            var row = CheckEvidence(item, ResolveMilitary(item, military), settings, documents);
            row.Origin = "IA_RESCUE";
            if (row.Severity == "success")
            {
                row.Status = "REVISÃO NECESSÁRIA";
                row.Severity = "warning";
                row.Notes = "Publicação recuperada semanticamente pela IA; a evidência foi localizada, mas exige revisão humana. " + row.Notes;
            }
            result.ExpectedItems.Add(item);
            result.Rows.Add(row);
        }
        foreach (var change in result.RubricChanges.Where(change => !change.HasBulletinEvidence))
            change.HasBulletinEvidence = added.Any(item => Digits(item.Cpf) == Digits(change.Cpf)
                && item.ExpectedCodes.Contains(change.Code, StringComparer.OrdinalIgnoreCase));
        result.Summary = BuildSummary(result.Rows, result.ExpectedItems.Count);
        return added.Count;
    }

    private static PaymentConferenceExpectedItem ToExpectedItem(AiRescuePublication source)
    {
        var codes = Regex.Matches(source.ExpectedRubricOrCode.ToUpperInvariant(), @"\b[A-Z]{2}\d{4}\b")
            .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new PaymentConferenceExpectedItem
        {
            Bulletin = source.BulletinNumber, BulletinDate = source.BulletinDate, BulletinPath = source.SourceFile,
            Page = Math.Max(1, source.Page), Name = source.MilitaryName, Cpf = source.Cpf, PrecCp = source.PrecCp,
            SectionTitle = source.Subject, PaymentType = string.IsNullOrWhiteSpace(source.Subject) ? "Outros Pagamentos" : source.Subject,
            PaymentMode = source.FinancialEffect, ReferencePeriod = source.ReferencePeriod,
            ExpectedCodes = codes, ExpectedRubricPrefix = string.Join("/", codes.Select(code => code[..2]).Distinct()),
            ExpectedAmount = source.ExpectedValue is > 0 ? (double)source.ExpectedValue.Value : 0,
            Context = source.SourceExcerpt, ReviewReason = "Interpretação semântica IA: " + source.ReasoningSummary,
            Origin = "IA_RESCUE", ParserConfidence = Math.Clamp(source.Confidence, 0, 1)
        };
    }

    private static bool SamePublication(PaymentConferenceExpectedItem left, PaymentConferenceExpectedItem right)
        => left.Bulletin.Equals(right.Bulletin, StringComparison.OrdinalIgnoreCase)
           && left.Page == right.Page
           && (Digits(left.Cpf).Length == 11 && Digits(left.Cpf) == Digits(right.Cpf)
               || Digits(left.PrecCp).Length >= 6 && Digits(left.PrecCp).TrimStart('0') == Digits(right.PrecCp).TrimStart('0')
               || Normalize(left.Name) == Normalize(right.Name))
           && (Normalize(left.PaymentType) == Normalize(right.PaymentType)
               || left.ExpectedCodes.Intersect(right.ExpectedCodes, StringComparer.OrdinalIgnoreCase).Any());
}
