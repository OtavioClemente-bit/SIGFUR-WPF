using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class SippesPersonnelConferenceService
{
    public static (List<SippesPersonnelConferenceRow> Rows, SippesPersonnelConferenceSummary Summary) Build(
        IReadOnlyList<SippesPersonnelRow> sippesRows,
        IReadOnlyList<MilitaryRecord> active,
        IReadOnlyList<LicensedTransferredRecord> licensedTransferred)
    {
        var activeByCpf = BuildLookup(active, x => x.Cpf);
        var activeByIdt = BuildLookup(active, x => x.MilitaryId);
        var activeByName = BuildNameLookup(active, x => x.Name);

        var ltByCpf = BuildLookup(licensedTransferred, x => x.Cpf);
        var ltByIdt = BuildLookup(licensedTransferred, x => x.MilitaryId);
        var ltByName = BuildNameLookup(licensedTransferred, x => x.Name);

        var matchedActiveIds = new HashSet<int>();
        var rows = new List<SippesPersonnelConferenceRow>();
        var nameOnly = 0;

        foreach (var sp in sippesRows)
        {
            var cpf = MilitaryFormatting.Digits(sp.Cpf);
            var idt = MilitaryFormatting.Digits(sp.MilitaryId);
            var nameKey = NormalizePersonName(sp.Name);

            if (TryGet(activeByCpf, cpf, out var activeMatch) || TryGet(activeByIdt, idt, out activeMatch))
            {
                matchedActiveIds.Add(activeMatch.Id);
                rows.Add(BuildRow(sp, "OK — ativo no SIGFUR e no SIPPES", "OK", "Ativos", activeMatch.Rank, activeMatch.Name, activeMatch.Cpf, activeMatch.MilitaryId, "Conferido por CPF/IDT", 90));
                continue;
            }

            if (TryGet(ltByCpf, cpf, out var ltMatch) || TryGet(ltByIdt, idt, out ltMatch))
            {
                rows.Add(BuildRow(sp, "Consta no SIPPES — em Lic./Transf. no SIGFUR", "ALERTA", "Licenciados/Transferidos", ltMatch.Rank, ltMatch.Name, ltMatch.Cpf, ltMatch.MilitaryId, "Conferido por CPF/IDT", 10));
                continue;
            }

            if (TryGetByName(activeByName, nameKey, sp.Name, out activeMatch))
            {
                matchedActiveIds.Add(activeMatch.Id);
                nameOnly++;
                rows.Add(BuildRow(sp, "Conferir — nome bate com ativo", "ATENÇÃO", "Ativos", activeMatch.Rank, activeMatch.Name, activeMatch.Cpf, activeMatch.MilitaryId, "Somente nome; conferir CPF/IDT", 60));
                continue;
            }

            if (TryGetByName(ltByName, nameKey, sp.Name, out ltMatch))
            {
                nameOnly++;
                rows.Add(BuildRow(sp, "Consta no SIPPES — nome bate com Lic./Transf.", "ALERTA", "Licenciados/Transferidos", ltMatch.Rank, ltMatch.Name, ltMatch.Cpf, ltMatch.MilitaryId, "Somente nome; conferir CPF/IDT", 20));
                continue;
            }

            rows.Add(BuildRow(sp, "Consta no SIPPES — fora do SIGFUR", "CRÍTICO", "Não localizado", string.Empty, string.Empty, string.Empty, string.Empty, "Não localizado em Ativos nem em Lic./Transf.", 0));
        }

        var sippesKeys = new HashSet<string>(sippesRows.SelectMany(x => new[]
        {
            Key("CPF", MilitaryFormatting.Digits(x.Cpf)),
            Key("IDT", MilitaryFormatting.Digits(x.MilitaryId)),
            Key("NOME", NormalizePersonName(x.Name))
        }).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);

        foreach (var person in active)
        {
            if (matchedActiveIds.Contains(person.Id)) continue;
            var keys = new[]
            {
                Key("CPF", MilitaryFormatting.Digits(person.Cpf)),
                Key("IDT", MilitaryFormatting.Digits(person.MilitaryId)),
                Key("NOME", NormalizePersonName(person.Name))
            }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (keys.Any(sippesKeys.Contains)) continue;

            rows.Add(new SippesPersonnelConferenceRow
            {
                Status = "Ativo SIGFUR não apareceu no SIPPES",
                Severity = "ATENÇÃO",
                SigfurSource = "Ativos",
                SigfurRank = person.Rank,
                SigfurName = person.Name,
                SigfurCpf = person.Cpf,
                SigfurMilitaryId = person.MilitaryId,
                MatchKind = "Não apareceu no SIPPES; conferir vínculo/folha",
                SortPriority = 30
            });
        }

        var summary = new SippesPersonnelConferenceSummary
        {
            SippesCount = sippesRows.Count,
            ActiveOkCount = rows.Count(x => x.Status.StartsWith("OK", StringComparison.OrdinalIgnoreCase)),
            ReceivingButLicensedTransferredCount = rows.Count(x => x.Status.Contains("Lic./Transf.", StringComparison.OrdinalIgnoreCase)),
            ReceivingOutsideSigfurCount = rows.Count(x => x.Status.Contains("fora do SIGFUR", StringComparison.OrdinalIgnoreCase)),
            ActiveMissingFromSippesCount = rows.Count(x => x.Status.StartsWith("Ativo SIGFUR", StringComparison.OrdinalIgnoreCase)),
            NameOnlyMatchCount = nameOnly,
            PaymentNormalCount = rows.Count(x => IsPaymentNormal(x.SippesPaymentStatus)),
            PaymentSuspendedCount = rows.Count(x => IsPaymentSuspended(x.SippesPaymentStatus)),
            PaymentOtherCount = rows.Count(x => HasPaymentStatus(x.SippesPaymentStatus) && !IsPaymentNormal(x.SippesPaymentStatus) && !IsPaymentSuspended(x.SippesPaymentStatus))
        };

        return (rows.OrderBy(x => x.SortPriority).ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(), summary);
    }

    public static string CsvCell(string? value)
        => (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace(';', ',').Trim();

    private static string Key(string prefix, string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : $"{prefix}:{value}";

    private static Dictionary<string, T> BuildLookup<T>(IEnumerable<T> rows, Func<T, string?> selector)
    {
        var dict = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var digits = MilitaryFormatting.Digits(selector(row) ?? string.Empty);
            if (digits.Length < 5 || dict.ContainsKey(digits)) continue;
            dict[digits] = row;
        }
        return dict;
    }

    private static Dictionary<string, T> BuildNameLookup<T>(IEnumerable<T> rows, Func<T, string?> selector)
    {
        var dict = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = NormalizePersonName(selector(row));
            if (string.IsNullOrWhiteSpace(key) || dict.ContainsKey(key)) continue;
            dict[key] = row;
        }
        return dict;
    }

    private static bool TryGet<T>(IReadOnlyDictionary<string, T> dict, string key, out T value)
    {
        if (!string.IsNullOrWhiteSpace(key) && dict.TryGetValue(key, out value!)) return true;
        value = default!;
        return false;
    }

    private static bool TryGetByName<T>(IReadOnlyDictionary<string, T> dict, string normalizedName, string rawName, out T value)
    {
        if (TryGet(dict, normalizedName, out value)) return true;

        // Último recurso: tolera pequenos cortes/quebras do SIPPES, mas só quando há pelo menos 3 partes do nome.
        var parts = NormalizePersonName(rawName).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            value = default!;
            return false;
        }

        foreach (var item in dict)
        {
            var score = parts.Count(p => item.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(p, StringComparer.OrdinalIgnoreCase));
            if (score >= Math.Min(parts.Length, 4))
            {
                value = item.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    private static SippesPersonnelConferenceRow BuildRow(SippesPersonnelRow sp, string status, string severity, string source, string rank, string name, string cpf, string militaryId, string matchKind, int priority)
    {
        var paymentStatus = sp.PaymentStatus;
        var paymentAlert = BuildPaymentAlert(status, paymentStatus);
        var finalSeverity = EscalateSeverity(severity, paymentAlert);
        return new SippesPersonnelConferenceRow
        {
            Status = status,
            Severity = finalSeverity,
            SippesMilitaryId = sp.MilitaryId,
            SippesCpf = sp.Cpf,
            SippesName = sp.Name,
            SippesRank = sp.Rank,
            SippesOm = sp.Om,
            SigfurSource = source,
            SigfurRank = rank,
            SigfurName = name,
            SigfurCpf = cpf,
            SigfurMilitaryId = militaryId,
            MatchKind = matchKind,
            ReportScript = sp.ReportScript,
            ReportPdfPath = sp.ReportPdfPath,
            ReportDownloadStatus = sp.ReportDownloadStatus,
            SippesPaymentStatus = paymentStatus,
            PaymentAlert = paymentAlert,
            SortPriority = priority
        };
    }

    private static string BuildPaymentAlert(string status, string paymentStatus)
    {
        if (!HasPaymentStatus(paymentStatus)) return string.Empty;
        var receivingProblem = status.Contains("Lic./Transf.", StringComparison.OrdinalIgnoreCase)
                               || status.Contains("fora do SIGFUR", StringComparison.OrdinalIgnoreCase);
        var activeOk = status.StartsWith("OK", StringComparison.OrdinalIgnoreCase) || status.Contains("ativo", StringComparison.OrdinalIgnoreCase);

        if (receivingProblem && IsPaymentNormal(paymentStatus)) return "CONFERIR — pagamento normal no SIPPES";
        if (receivingProblem && IsPaymentSuspended(paymentStatus)) return "OK — pagamento suspenso";
        if (activeOk && IsPaymentSuspended(paymentStatus)) return "ATENÇÃO — ativo com pagamento suspenso";
        if (activeOk && IsPaymentNormal(paymentStatus)) return "OK — pagamento normal";
        return paymentStatus;
    }

    private static string EscalateSeverity(string severity, string paymentAlert)
    {
        if (paymentAlert.Contains("CONFERIR", StringComparison.OrdinalIgnoreCase) && paymentAlert.Contains("PAGAMENTO NORMAL", StringComparison.OrdinalIgnoreCase)) return "CRÍTICO";
        if (paymentAlert.Contains("ATENÇÃO", StringComparison.OrdinalIgnoreCase) && !string.Equals(severity, "CRÍTICO", StringComparison.OrdinalIgnoreCase)) return "ATENÇÃO";
        return severity;
    }

    private static bool HasPaymentStatus(string? value)
    {
        var text = Normalize(value);
        return !string.IsNullOrWhiteSpace(text) && !text.Contains("nao baixado") && !text.Contains("nao lido") && !text.Contains("nao localizada");
    }

    private static bool IsPaymentSuspended(string? value) => Normalize(value).Contains("pagamento suspenso");

    private static bool IsPaymentNormal(string? value)
    {
        var text = Normalize(value);
        return text.Contains("pagamento normal") || (text.Contains("pagamento") && !text.Contains("suspenso") && !text.Contains("bloqueado") && !text.Contains("transferido"));
    }

    private static string Normalize(string? value)
    {
        var text = StripAccents(value ?? string.Empty).ToLowerInvariant();
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string NormalizePersonName(string? value)
    {
        var text = StripAccents(value ?? string.Empty).ToUpperInvariant();
        text = Regex.Replace(text, @"\b(?:SD\s+RCR|SD\s+EV|SD\s+EF|SOLDADO\s+EFETIVO\s+PROFISSIONAL|SOLDADO\s+EFETIVO\s+VARIAVEL|SOLDADO\s+RECRUTA|SOLDADO|CABO|CB|ASP\s+OF|CAPITAO|CAP|MAJOR|MAJ|TENENTE\s+CORONEL|TEN\s+CEL|CORONEL|CEL|SUBTENENTE|S\s*TEN|\d+\s*O?\s*TENENTE|\d+\s*O?\s*SGT|SARGENTO)\b", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"[^A-Z ]", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string StripAccents(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
