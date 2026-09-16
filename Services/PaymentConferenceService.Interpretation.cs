using System.Text.Json;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed partial class PaymentConferenceService
{
    private static readonly Lazy<Dictionary<string, List<int>>> ManualCodes = new(() =>
    {
        using var stream = typeof(PaymentConferenceService).Assembly.GetManifestResourceStream(
            "SIGFUR.Wpf.Resources.PaymentConference.sippes-rubrics-2026-07-17.json")!;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("codes").Deserialize<Dictionary<string, List<int>>>()!;
    });

    private static string ManualSource(IEnumerable<string> codes)
    {
        var pages = codes.Where(ManualCodes.Value.ContainsKey).SelectMany(c => ManualCodes.Value[c]).Distinct().Order().ToList();
        return pages.Count == 0 ? "Código expresso no boletim; sem referência localizada no manual fornecido."
            : "Manual Técnico do SIPPES, 17/07/2026, pág. " + string.Join(", ", pages);
    }

    private static readonly Regex HeadingPattern = new(@"(?m)^\s*[a-z]\.[ \t]+(?<title>[^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex PersonPattern = new(
        @"^(?:\d+[.)]\s*)?(?:Militar\s*:\s*)?(?<rank>(?:S\s*Ten|Sub\s*Ten|Asp(?:\s+Of)?|Cap|Maj|Ten\s*Cel|Cel|[123][º°o]?\s*(?:Ten|Sgt)|Cb|Sd)(?:\s*(?:Ef\s*(?:Profl|Vrv)|EP|EV))?)\s*(?<name>[A-ZÀ-Ý][A-ZÀ-Ý\s.'’\-]{3,}?)(?=\s+(?:Prec|CPF)\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<PaymentConferenceExpectedItem> ParsePublications(string path, IReadOnlyList<string> pages,
        PaymentConferenceSettings? settings, CancellationToken token)
    {
        var metadata = ExtractBulletinMetadata(pages, path);
        // Keep physical page locations before removing continuation headers.
        var lines = pages.SelectMany((p, i) => p.Split('\n').Select(l => (Text: OneLine(l), Page: i + 1)))
            .Where(l => l.Text.Length > 0 && !IsContinuationNoise(Normalize(l.Text)) && !Regex.IsMatch(l.Text, @"^Pag\s+n", RegexOptions.IgnoreCase)).ToList();
        var start = lines.FindIndex(l => Normalize(l.Text).Contains("PAGAMENTO PESSOAL"));
        if (start < 0) start = 0;
        var end = lines.FindIndex(start, l => Regex.IsMatch(Normalize(l.Text), @"^4[ªAº]?\s+PARTE|^JUSTICA E DISCIPLINA"));
        if (end < 0) end = lines.Count;
        var boundaries = Enumerable.Range(start, end - start).Where(i => HeadingPattern.IsMatch(lines[i].Text)
            && !Regex.IsMatch(Normalize(HeadingPattern.Match(lines[i].Text).Groups["title"].Value), @"^(RENDIMENTOS|DESCONTOS|RECEITAS|DESPESAS)$")).ToList();
        if (boundaries.Count == 0) boundaries.Add(start);
        boundaries.Add(end);
        var result = new List<PaymentConferenceExpectedItem>();
        var occurrences = new Dictionary<string, int>();
        for (var s = 0; s < boundaries.Count - 1; s++)
        {
            token.ThrowIfCancellationRequested();
            var first = boundaries[s]; var last = boundaries[s + 1];
            var title = HeadingPattern.Match(lines[first].Text).Groups["title"].Value;
            if (title.Length == 0) title = "PAGAMENTO PESSOAL";
            var people = Enumerable.Range(first, last - first)
                .Select(i => (Index: i, Match: PersonPattern.Match(lines[i].Text)))
                .Where(p => p.Match.Success).ToList();
            var capturedCpfs = new HashSet<string>();
            for (var p = 0; p < people.Count; p++)
            {
                var (at, match) = people[p];
                var next = p + 1 < people.Count ? people[p + 1].Index : last;
                var own = string.Join("\n", lines.Skip(at).Take(next - at).Select(l => l.Text));
                var cpf = CpfRegex().Match(own).Value;
                var prec = PrecRegex().Match(own).Groups[1].Value;
                // Do not borrow identity from a later paragraph/person.
                var idLine = Enumerable.Range(at, next - at).FirstOrDefault(i => CpfRegex().IsMatch(lines[i].Text) || PrecRegex().IsMatch(lines[i].Text), -1);
                if (idLine < 0 && match.Groups["name"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) continue;
                var ownEnd = next;
                for (var i = Math.Max(at + 1, idLine + 1); i < next; i++)
                    if (Regex.IsMatch(Normalize(lines[i].Text), @"^(EM CONSEQUENCIA|SEJA|SEJAM|SOLICITO)\b")) { ownEnd = i; break; }
                var sharedStart = first;
                for (var i = at - 1; i > first; i--)
                {
                    if (Regex.IsMatch(Normalize(lines[i].Text), @"^(SEJA|SEJAM|SOLICITO)\b")) { sharedStart = i; break; }
                    if (i < at - 1 && CpfRegex().IsMatch(lines[i].Text)) { sharedStart = first; break; }
                }
                var firstPersonInBlock = people.Where(x => x.Index >= sharedStart).First().Index;
                var shared = string.Join("\n", lines.Skip(sharedStart).Take(firstPersonInBlock - sharedStart).Select(l => l.Text));
                var local = string.Join("\n", lines.Skip(at).Take(ownEnd - at).Select(l => l.Text));
                var context = shared + "\n" + local;
                var key = Digits(cpf).Length == 11 ? Digits(cpf) : Normalize(match.Groups["name"].Value);
                occurrences.TryGetValue(key, out var occurrence); occurrences[key] = ++occurrence;
                capturedCpfs.Add(Digits(cpf));
                var item = new PaymentConferenceExpectedItem
                {
                    Bulletin = metadata.Bulletin, BulletinDate = metadata.Date, BulletinPath = path,
                    Page = lines[at].Page, DocumentOccurrence = occurrence, SectionTitle = title,
                    Name = CleanName(match.Groups["name"].Value), Rank = CleanRank(match.Groups["rank"].Value),
                    Cpf = MilitaryFormatting.FormatCpf(cpf), PrecCp = Digits(prec), Context = CleanContext(context)
                };
                foreach (var expanded in InterpretPublication(item, shared, local))
                    if (ShouldInclude(expanded.PaymentType, settings)) result.Add(expanded);
            }
            // Every identity-bearing publication stays visible even when its name layout is unfamiliar.
            foreach (var i in Enumerable.Range(first, last - first).Where(i => CpfRegex().IsMatch(lines[i].Text)))
            {
                var cpf = CpfRegex().Match(lines[i].Text).Value;
                if (!capturedCpfs.Add(Digits(cpf))) continue;
                result.Add(new PaymentConferenceExpectedItem
                {
                    Bulletin = metadata.Bulletin, BulletinDate = metadata.Date, BulletinPath = path, Page = lines[i].Page,
                    SectionTitle = title, Name = "Identidade publicada — revisar nome", Cpf = MilitaryFormatting.FormatCpf(cpf),
                    PrecCp = Digits(PrecRegex().Match(lines[i].Text).Groups[1].Value),
                    Context = string.Join("\n", lines.Skip(Math.Max(first, i - 2)).Take(5).Select(l => l.Text)),
                    ReviewReason = "CPF identificado, mas a estrutura do nome não foi reconhecida. O item foi preservado para revisão."
                });
            }
        }
        return result;
    }

    private static IEnumerable<PaymentConferenceExpectedItem> InterpretPublication(PaymentConferenceExpectedItem item, string shared, string local)
    {
        var title = Normalize(item.SectionTitle).Replace('-', ' ');
        var blob = Normalize(item.Context).Replace('-', ' ');
        item.AdministrativeOnly = title.Contains("FICHA FINANCEIRA") || title.Contains("SITUACAO PAGAMENTO") || title.Contains("SITUACAO DE PAGAMENTO")
            || Regex.IsMatch(blob, @"\b(?:SEJA\s+SUSPENDIDO|SEJA\s+SUSPENSO|SUSPENSAO\s+D[OE])\s+(?:O\s+)?PAGAMENTO\b");
        if (item.AdministrativeOnly)
        {
            item.PaymentType = "Alteração cadastral/financeira";
            item.ReviewReason = "Ato de alteração: verificar no SIPPES/ficha financeira. A presença de rubricas no contracheque não comprova a alteração solicitada.";
            yield return item; yield break;
        }
        item.PaymentType = BenefitType(title);
        if (item.PaymentType == "Outros Pagamentos") item.PaymentType = BenefitType(blob);
        var modeText = title + " " + blob;
        item.ExpectedRubricPrefix = Regex.IsMatch(modeText, @"ANULAR|ANULACAO|DEVOLU|DESCONT|RESSARC") ? "DR"
            : modeText.Contains("EXERCICIO ANTERIOR") || modeText.Contains("EXERCICIOS ANTERIORES") ? "ER"
            : title.Contains("DIFERENCA") || blob.Contains("DIFERENCA A SER PAGA") ? "FR"
            : modeText.Contains("ATRASAD") ? "AR" : "NR";
        item.PaymentMode = item.ExpectedRubricPrefix switch { "DR" => "Devolução/desconto", "ER" => "Exercício anterior", "FR" => "Diferença", "AR" => "Atrasado", _ => "Normal/saque" };
        var reference = Regex.Match(blob, @"MES(?:/ANO)? DE REFERENCIA\s*:\s*([A-Z]+|\d{1,2})(?:\s+(20\d{2}))?");
        if (reference.Success)
        {
            var year = reference.Groups[2].Success ? reference.Groups[2].Value : Regex.Match(blob, @"ANO DE REFERENCIA\s*:\s*(20\d{2})").Groups[1].Value;
            item.ReferencePeriod = reference.Groups[1].Value + " " + year;
        }
        // Only an explicit payment month constrains the chosen payroll; reference months are entitlement parameters.
        var payment = Regex.Match(blob, @"PAGAMENTO (?:DO |DE |NO )?MES DE\s+([A-Z]+)\s+(?:DE\s+)?(20\d{2}|\d{2})\b");
        if (payment.Success)
        {
            item.PaymentMonth = MonthNumber(payment.Groups[1].Value);
            item.PaymentYear = int.Parse(payment.Groups[2].Value); if (item.PaymentYear < 100) item.PaymentYear += 2000;
        }
        var explicitCodes = RubricCodeRegex().Matches(Normalize(local)).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (explicitCodes.Count == 0)
            explicitCodes = RubricCodeRegex().Matches(Normalize(shared)).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (item.PaymentType == "Férias" && blob.Contains("INDENIZACAO DE FERIAS") && blob.Contains("ADICIONAL") && explicitCodes.Count == 0)
        {
            foreach (var (type, code, pattern) in new[] {
                ("Adicional de férias — ajuste de contas", "AR0096", @"ADICIONAL DE FERIAS[^\n]*"),
                ("Indenização de férias", "AR0094", @"INDENIZACAO DE FERIAS[^\n]*") })
            {
                var copy = JsonSerializer.Deserialize<PaymentConferenceExpectedItem>(JsonSerializer.Serialize(item))!;
                copy.Id = Guid.NewGuid().ToString("N"); copy.PaymentType = type; copy.ExpectedRubricPrefix = "AR";
                copy.ExpectedCodes = [code]; copy.PaymentMode = "Ajuste de contas";
                copy.ExpectedAmount = ExtractPublishedAmount(Regex.Matches(NormalizeLines(item.Context), pattern)
                    .Select(m => m.Value).LastOrDefault(MoneyRegex().IsMatch) ?? "");
                copy.ExpectedRubricRule = ManualSource(copy.ExpectedCodes);
                yield return copy;
            }
            yield break;
        }
        item.ExpectedCodes = explicitCodes.Count > 0 ? explicitCodes : CodesForBenefit(item.PaymentType, item.ExpectedRubricPrefix, blob);
        item.ExpectedAmount = ExtractPublishedAmount(local);
        if (item.ExpectedAmount == 0) item.ExpectedAmount = ExtractPublishedAmount(shared);
        item.ExpectedRubricRule = ManualSource(item.ExpectedCodes);
        if (explicitCodes.Count > 0)
        {
            item.ExpectedRubricRule = "Código expresso no boletim. " + item.ExpectedRubricRule;
            item.ExpectedRubricPrefix = string.Join("/", explicitCodes.Select(c => c[..2]).Distinct());
            var unknown = explicitCodes.Where(c => !ManualCodes.Value.ContainsKey(c)).ToList();
            if (unknown.Count > 0) item.ReviewReason = "Código publicado sem referência no manual fornecido: " + string.Join(", ", unknown) + ". Não foi substituído automaticamente por outro código.";
            if (explicitCodes.Count > 1) item.ReviewReason += " Há vários códigos no mesmo bloco; verificar cada lançamento e seu valor.";
        }
        if (item.ExpectedCodes.Count == 0) item.ReviewReason = "Não há código explícito nem regra suficiente no manual para identificar a rubrica com segurança.";
        yield return item;
    }

    private static string BenefitType(string text)
        => text.Contains("ALIMENTACAO") ? "Auxílio-Alimentação"
        : text.Contains("TRANSPORTE") ? "Auxílio-Transporte"
        : text.Contains("FARDAMENTO") ? "Auxílio-Fardamento"
        : text.Contains("PRE ESCOLAR") || text.Contains("PRE-ESCOLAR") ? "Assistência Pré-Escolar"
        : text.Contains("NATALIDADE") ? "Auxílio-Natalidade"
        : text.Contains("HABILITACAO") ? "Adicional Habilitação"
        : text.Contains("REPRESENTACAO") || text.Contains("GRAT REP") ? "Gratificação de Representação"
        : text.Contains("NATALINO") || text.Contains("ADICIONAL NATAL") ? "Adicional Natalino"
        : text.Contains("FERIAS") ? "Férias" : "Outros Pagamentos";

    private static List<string> CodesForBenefit(string type, string prefix, string context)
    {
        string[] suffixes = type switch
        {
            "Auxílio-Transporte" => ["0095"],
            "Adicional Habilitação" => ["0003"],
            "Gratificação de Representação" => ["0061"],
            "Auxílio-Alimentação" => context.Contains("5X") ? ["0052"] : context.Contains("10X") ? ["0053"] : ["0058"],
            "Auxílio-Fardamento" => context.Contains("1,5") ? ["0057"] : ["0056"],
            "Assistência Pré-Escolar" => ["0077"],
            "Auxílio-Natalidade" => prefix == "ER" ? ["0081"] : ["0043"],
            "Adicional Natalino" => context.Contains("AJUSTE") ? ["0070"] : [],
            "Férias" => context.Contains("RAIO") ? ["0091"] : ["0092"],
            _ => []
        };
        if (type == "Adicional Natalino" && suffixes.Contains("0070")) prefix = "NR";
        return suffixes.Select(s => prefix + s).Where(ManualCodes.Value.ContainsKey).ToList();
    }

    private static string NormalizeLines(string text) => string.Join("\n", text.Split('\n').Select(Normalize));

    private static double ExtractPublishedAmount(string text)
    {
        var lines = text.Split('\n');
        foreach (var label in new[] { "DIFERENCA A SER PAGA", "VALOR TOTAL A SER DESCONTADO", "VALOR TOTAL A DESCONTAR", "VALOR TOTAL", "PARCELA" })
        {
            var line = lines.LastOrDefault(l => Normalize(l).Contains(label) && MoneyRegex().IsMatch(l));
            if (line is not null) return ParseMoney(MoneyRegex().Matches(line).Last().Value);
        }
        var values = MoneyRegex().Matches(text).Select(m => ParseMoney(m.Value)).Distinct().ToList();
        return values.Count == 1 && !Normalize(text).Contains("VALOR DIARIO") ? values[0] : 0;
    }
}
