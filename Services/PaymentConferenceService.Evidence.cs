using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed partial class PaymentConferenceService
{
    public static string ValidatePayrollIdentity(string text, string cpf, int month, int year)
    {
        var header = PayrollHeader(text);
        var periods = ReadPayrollPeriods(header);
        if (periods.Count != 1 || periods[0] != (month, year)) return "Competência do cabeçalho não corresponde ao mês/ano selecionado.";
        var cpfs = CpfRegex().Matches(header).Select(m => Digits(m.Value)).Distinct().ToList();
        if (cpfs.Count != 1 || cpfs[0] != Digits(cpf)) return "CPF do cabeçalho não corresponde de forma única ao cadastro.";
        return "";
    }
    public static (string Cpf, int Month, int Year)? PayrollDescriptor(string text)
    {
        var header = PayrollHeader(text); var periods = ReadPayrollPeriods(header);
        var cpfs = CpfRegex().Matches(header).Select(m=>Digits(m.Value)).Distinct().ToList();
        return periods.Count == 1 && cpfs.Count == 1 ? (cpfs[0], periods[0].Month, periods[0].Year) : null;
    }
    private sealed record ConferencePdf(string Path, string Text, string Header, List<string> Cpfs, List<string> Precs,
        List<(int Month, int Year)> Periods, List<PaystubRubric> Rubrics, string Error);

    private async Task<List<ConferencePdf>> ReadConferencePdfsAsync(PaymentConferenceSettings settings,
        PaymentConferenceResult result, IProgress<string>? progress, CancellationToken token)
    {
        var folder = string.IsNullOrWhiteSpace(settings.PaystubFolder) ? _paths.PaystubsDirectory : settings.PaystubFolder;
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Pasta de contracheques não encontrada: " + folder);
        var files = Directory.EnumerateFiles(folder, "*.pdf*", SearchOption.AllDirectories).Where(LooksLikePaystubPdfFile)
            .Where(p => !Regex.IsMatch(Normalize(p), @"FICHA[ _-]*FINANCEIRA|ESPELHO[ _-]*(?:DE[ _-]*)?CONTRACHEQUE")).Order().ToList();
        var documents = new List<ConferencePdf>();
        var periods = await ReadPeriodIndexAsync(files, progress, token);
        var selected = periods.Where(p => p.Month == settings.Month && p.Year == settings.Year && p.Error.Length == 0).ToList();
        result.Inventory.FoundPaystubs = selected.Count;
        result.OtherPeriodFiles = periods.Count(p => p.Year > 0 && (p.Month != settings.Month || p.Year != settings.Year));
        result.UnclassifiedFiles = periods.Count(p => p.Error.Length > 0);
        if (result.UnclassifiedFiles > 0)
            result.Warnings.Add($"{result.UnclassifiedFiles} PDF(s) sem competência confirmada ficaram fora da conferência: "
                + string.Join("; ", periods.Where(p => p.Error.Length > 0).Take(5).Select(p => $"{Path.GetFileName(p.Path)} — {p.Error}")));
        for (var i = 0; i < selected.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var path = selected[i].Path;
            progress?.Report($"Lendo contracheques de {settings.Month:00}/{settings.Year}: {i + 1}/{selected.Count} — {Path.GetFileName(path)}");
            try
            {
                var text = await _pdfText.ExtractAsync(path, token);
                var document = ParseConferencePdf(path, text);
                if (document.Periods.Count != 1 || document.Periods[0] != (settings.Month, settings.Year))
                {
                    result.Warnings.Add($"{Path.GetFileName(path)}: competência não confirmada na releitura; arquivo excluído.");
                    result.UnclassifiedFiles++;
                    continue;
                }
                documents.Add(document);
                result.Inventory.ReadPaystubs++;
                result.PaystubFiles.Add(new Models.PaymentConferencePaystubFile
                {
                    Path = path, Cpf = document.Cpfs.FirstOrDefault() ?? "", Month = settings.Month, Year = settings.Year
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Warnings.Add($"Não foi possível ler o contracheque de {settings.Month:00}/{settings.Year}, {Path.GetFileName(path)}: {ex.Message}");
                result.Inventory.UnreadableDocuments.Add(Path.GetFileName(path) + ": " + ex.Message);
            }
        }
        if (files.Count == 0) result.Warnings.Add("A pasta selecionada não contém PDFs de contracheque.");
        else if (documents.Count == 0) result.Warnings.Add($"Nenhum contracheque com competência {settings.Month:00}/{settings.Year} confirmada no PDF. Os boletins marcados foram preservados, mas não há folha dessa competência para comparar.");
        return documents;
    }

    private static ConferencePdf ParseConferencePdf(string path, string text)
    {
        var header = PayrollHeader(text);
        // CPF and PREC may occupy a separate row from their labels. Require a unique identity in the document.
        var cpfs = CpfRegex().Matches(header).Select(m => Digits(m.Value)).Distinct().ToList();
        var precs = PrecRegex().Matches(header).Select(m => Digits(m.Groups[1].Value).TrimStart('0')).Where(p => p.Length >= 6).Distinct().ToList();
        var periods = ReadPayrollPeriods(header);
        return new ConferencePdf(path, text, header, cpfs, precs, periods, ReadRubrics(text),
            string.IsNullOrWhiteSpace(text) ? "PDF sem texto legível; requer OCR ou outro arquivo." : "");
    }

    private static List<(int Month, int Year)> ReadPayrollPeriods(string header)
    {
        var result = new List<(int, int)>();
        var normalized = NormalizeLines(header);
        // Only payroll/reference labels and standalone month/year lines; issue/access dates are not payroll periods.
        var candidates = Regex.Matches(normalized,
            @"(?m)(?:COMPETENCIA|MES\s*/\s*ANO|MES(?:\s+DE\s+PAGAMENTO)?|FOLHA(?:\s+DE\s+PAGAMENTO)?|REFERENCIA)\s*[:\-]?\s*(?:(?:NORMAL|SUPLEMENTAR)\s*[-:]?\s*)?(?<date>(?:[A-Z]+|\d{1,2})\s*[/\- ]\s*(?:20\d{2}|\d{2}))\b|^\s*(?<date>(?:[A-Z]+|\d{1,2})\s*[/\- ]\s*20\d{2})\s*$");
        foreach (Match candidate in candidates)
        {
            var parts = Regex.Match(candidate.Groups["date"].Value, @"([A-Z]+|\d{1,2})\s*[/\- ]\s*(20\d{2}|\d{2})");
            var month = int.TryParse(parts.Groups[1].Value, out var number) ? number : MonthNumber(parts.Groups[1].Value);
            var year = int.Parse(parts.Groups[2].Value); if (year < 100) year += 2000;
            if (month is >= 1 and <= 12) result.Add((month, year));
        }
        // PdfPig rebuilds the SIPPES header as a row of labels followed by a row of values.
        // "CPF: CATEGORIA: FOLHA:" / "... Militar da ativa NORMAL- Junho/2026".
        if (normalized.Contains("FOLHA"))
            foreach (Match candidate in Regex.Matches(normalized, @"\b(?:NORMAL|SUPLEMENTAR)\s*[-:]\s*([A-Z]+|\d{1,2})\s*/\s*(20\d{2})\b"))
            {
                var month = int.TryParse(candidate.Groups[1].Value, out var numeric) ? numeric : MonthNumber(candidate.Groups[1].Value);
                if (month is >= 1 and <= 12) result.Add((month, int.Parse(candidate.Groups[2].Value)));
            }
        // Legacy CPEx places MÊS and its value in a right-hand column. The layout extractor can
        // merge the value with a ministry heading (e.g. "EXÉRCITO MAIO / 2026").
        if (normalized.Contains("MES") && (normalized.Contains("COMPROVANTE MENSAL") || normalized.Contains("CONTRACHEQUE")))
            foreach (var line in normalized.Split('\n').Where(l => !Regex.IsMatch(l, @"EMISSAO|EMITIDO|ACESSADO|DATA|PRACA")))
                foreach (Match candidate in Regex.Matches(line, @"(?<![A-Z0-9/])([A-Z]+)\s*/\s*(20\d{2})\b"))
                {
                    var month = MonthNumber(candidate.Groups[1].Value);
                    if (month is >= 1 and <= 12) result.Add((month, int.Parse(candidate.Groups[2].Value)));
                }
        return result.Distinct().ToList();
    }

    private static string CompactIdentity(string text) => Regex.Replace(Normalize(text), @"[^A-Z]", "");

    private static bool MatchesDocumentIdentity(PaymentConferenceExpectedItem item, MilitaryRecord? military, ConferencePdf pdf)
    {
        var cpf = Digits(item.Cpf); if (cpf.Length != 11) cpf = Digits(military?.Cpf);
        var prec = Digits(item.PrecCp).TrimStart('0'); if (prec.Length < 6) prec = Digits(military?.PrecCp).TrimStart('0');
        if (cpf.Length == 11 && pdf.Cpfs.Count > 0) return pdf.Cpfs.Count == 1 && pdf.Cpfs[0] == cpf
            && (prec.Length < 6 || pdf.Precs.Count == 0 || pdf.Precs.Count == 1 && pdf.Precs[0] == prec);
        if (prec.Length >= 6 && pdf.Precs.Count > 0) return pdf.Precs.Count == 1 && pdf.Precs[0] == prec;
        // An exact full name is useful to locate a candidate, but does not establish identity for an automatic confirmation.
        return false;
    }

    private static PaymentConferenceResultRow CheckEvidence(PaymentConferenceExpectedItem item, MilitaryRecord? military,
        PaymentConferenceSettings settings, IReadOnlyList<ConferencePdf> documents)
    {
        var row = new PaymentConferenceResultRow
        {
            ItemId = item.Id,
            Origin = item.Origin,
            PublishedName = item.Name,
            ConferencePeriod = $"{settings.Month:00}/{settings.Year}",
            Bulletin = item.Bulletin, BulletinDate = item.BulletinDate, BulletinPath = item.BulletinPath,
            BulletinPage = item.Page, DocumentOccurrence = item.DocumentOccurrence, SectionTitle = item.SectionTitle,
            PaymentType = item.PaymentType, PaymentMode = item.PaymentMode, ExpectedRubricPrefix = item.ExpectedRubricPrefix,
            ExpectedCodesText = item.ExpectedCodesText, RuleSource = item.ExpectedRubricRule,
            Military = string.IsNullOrWhiteSpace(item.MatchedMilitaryName) ? item.IdentityText : item.MatchedMilitaryName,
            Rank = item.Rank, Cpf = item.Cpf, PrecCp = item.PrecCp, MilitaryId = item.MatchedMilitaryId,
            ExpectedAmount = item.ExpectedAmount, HasExpectedAmount = item.ExpectedAmount > 0,
            Context = item.Context, ReferencePeriod = item.ReferencePeriod
        };
        PaymentConferenceResultRow Finish(string status, string severity, string notes)
        { row.Status = status; row.Severity = severity; row.Notes = notes; return row; }
        if (item.AdministrativeOnly)
        {
            var candidates = documents.Where(d => d.Periods.Count == 1 && d.Periods[0] == (settings.Month, settings.Year)
                && MatchesDocumentIdentity(item, military, d)).GroupBy(d => HashText(d.Text)).Select(g => g.First()).ToList();
            if (candidates.Count == 1) row.PaystubPath = candidates[0].Path;
            return Finish("ATO CADASTRAL", "info", item.ReviewReason);
        }
        if (item.PaymentMonth > 0 && item.PaymentYear > 0 && (item.PaymentMonth != settings.Month || item.PaymentYear != settings.Year))
            return Finish("OUTRA COMPETÊNCIA", "info", $"O boletim determina pagamento em {item.PaymentMonth:00}/{item.PaymentYear}; a folha selecionada é {settings.Month:00}/{settings.Year}.");
        // Defence in depth: evidence from another month must not affect even warnings or name candidates.
        documents = documents.Where(d => d.Periods.Count == 1 && d.Periods[0] == (settings.Month, settings.Year)).ToList();
        var identities = documents.Where(d => MatchesDocumentIdentity(item, military, d)).ToList();
        var eligible = identities.Where(d => d.Periods.Count == 1 && d.Periods[0] == (settings.Month, settings.Year))
            .GroupBy(d => HashText(d.Text)).Select(g => g.First()).ToList();
        if (eligible.Count > 1) return Finish("REVISÃO NECESSÁRIA", "warning", "Há contracheques diferentes para a mesma identidade e competência. Selecione uma pasta com a versão válida para evitar escolher uma folha arbitrariamente.");
        if (eligible.Count == 0)
        {
            if (identities.Any(d => d.Periods.Count != 1))
                return Finish("REVISÃO NECESSÁRIA", "warning", "Identidade localizada, mas a competência não pôde ser confirmada de forma única no cabeçalho do PDF.");
            var name = CompactIdentity(item.Name);
            var uncertain = documents.Any(d => d.Error.Length > 0 ||
                (!identities.Contains(d) && name.Length >= 10 && CompactIdentity(d.Header + " " + Path.GetFileName(d.Path)).Contains(name)));
            return uncertain ? Finish("REVISÃO NECESSÁRIA", "warning", "Há PDF sem leitura completa ou candidato por nome sem identidade/competência confirmada. Não é possível afirmar ausência do contracheque.")
                : Finish("SEM CONTRACHEQUE", "warning", $"Não foi localizado PDF com CPF/PREC e competência {settings.Month:00}/{settings.Year} confirmados na pasta selecionada.");
        }
        var pdf = eligible[0]; row.PaystubPath = pdf.Path;
        row.IdentityEvidence = $"Identidade confirmada por {(pdf.Cpfs.Count == 1 ? "CPF" : "PREC")}; competência {settings.Month:00}/{settings.Year} lida no PDF. Cadastro: {item.MatchStatus}.";
        row.OtherRubrics = string.Join("; ", pdf.Rubrics.Select(r => r.CompactText));
        if (pdf.Error.Length > 0 || pdf.Rubrics.Count == 0) return Finish("REVISÃO NECESSÁRIA", "warning", "O PDF foi localizado, mas sua tabela de rubricas não pôde ser lida. Ausência de leitura não comprova ausência de lançamento.");
        var matches = FindRubricMatches(item, pdf.Rubrics, true);
        if (settings.AcceptRubricPresence)
        {
            if (matches.Count == 0) matches = FindRubricsByDescription(item, pdf.Rubrics);
            if (matches.Count > 0)
            {
                row.PresenceOnly = true;
                row.RubricsFound = string.Join("; ", matches.Select(r => r.CompactText));
                row.OtherRubrics = string.Join("; ", pdf.Rubrics.Except(matches).Select(r => r.CompactText));
                row.HasPaidAmount = matches.Count == 1 && matches[0].MoneyValues.Count == 1;
                if (row.HasPaidAmount) row.PaidAmount = matches[0].Value;
                var exact = matches.All(r => item.ExpectedCodes.Contains(r.Code, StringComparer.OrdinalIgnoreCase));
                var note = (exact ? "Presença confirmada pelo código." : "Presença confirmada pela descrição da rubrica no contracheque; o código difere do esperado.")
                    + " Critério: benefício presente na folha selecionada. Valor, referência e atribuição a cada publicação não foram validados.";
                if (row.HasPaidAmount && row.HasExpectedAmount && Math.Abs(row.PaidAmount - row.ExpectedAmount) > settings.Tolerance)
                    note += $" Valores diferentes: publicado {row.ExpectedAmountText}; no PDF {row.PaidAmountText}.";
                return Finish("ACHOU RUBRICA", "success", note);
            }
        }
        row.RubricsFound = string.Join("; ", matches.Select(r => r.CompactText));
        row.HasPaidAmount = matches.Count == 1 && matches[0].MoneyValues.Count == 1;
        if (row.HasPaidAmount) row.PaidAmount = matches.Sum(r => r.Value);
        row.OtherRubrics = string.Join("; ", pdf.Rubrics.Except(matches).Select(r => r.CompactText));
        if (item.ReviewReason.Length > 0) return Finish("REVISÃO NECESSÁRIA", "warning", item.ReviewReason + (matches.Count > 0 ? " Código publicado localizado no PDF." : " Código publicado não localizado no PDF."));
        if (matches.Count > 1) return Finish("REVISÃO NECESSÁRIA", "warning", "Há mais de um lançamento compatível no PDF. Confira os parâmetros de referência/parcela antes de atribuir ou somar os valores à publicação.");
        if (matches.Count == 0)
        {
            var alternatives = FindRubricMatches(item, pdf.Rubrics, false);
            if (alternatives.Count > 0) return Finish("NATUREZA DIFERENTE", "warning", "Encontrada a família da rubrica com outra natureza: " + string.Join("; ", alternatives.Select(r => r.CompactText)) + ". Não comprova o lançamento publicado.");
            return Finish("NÃO ACHOU RUBRICA", "danger", "Código esperado não localizado no contracheque identificado. Este resultado se limita à folha selecionada; não comprova se houve lançamento pendente de processamento no SIPPES.");
        }
        if (row.HasExpectedAmount && !row.HasPaidAmount) return Finish("REVISÃO NECESSÁRIA", "warning", "Rubrica localizada, mas a disposição dos valores no PDF não permite atribuir um valor único ao lançamento.");
        if (row.HasExpectedAmount && Math.Abs(row.PaidAmount - row.ExpectedAmount) > Math.Clamp(settings.Tolerance, 0, 1))
            return Finish("VALOR DIVERGENTE", "danger", "Código localizado, mas o valor difere do valor publicado. Verifique referências, parcelas e outros lançamentos agrupados no mesmo código.");
        return Finish("ACHOU RUBRICA", "success", row.HasExpectedAmount
            ? "Código e valor publicado conferem na folha selecionada; confira o parâmetro de referência no SIPPES quando não estiver visível no contracheque."
            : "Código publicado/previsto localizado na folha selecionada. O boletim não informa valor individual inequívoco; o valor não foi validado.");
    }

    private static void FlagSharedEvidence(PaymentConferenceResult result)
    {
        foreach (var group in result.Rows.Where(r => r.PaystubPath.Length > 0 && r.RubricsFound.Length > 0)
                     .SelectMany(r => RubricCodeRegex().Matches(r.RubricsFound).Select(m => (Row: r, Code: m.Value)))
                     .GroupBy(x => (x.Row.PaystubPath, x.Code)).Where(g => g.Select(x => x.Row).Distinct().Count() > 1))
        {
            foreach (var row in group.Select(x => x.Row).Distinct().Where(r => r.Severity == "success" || r.Status == "VALOR DIVERGENTE"))
            {
                if (row.PresenceOnly)
                {
                    row.Notes += " A mesma rubrica aparece em outras publicações deste militar; a presença está confirmada, mas confira a distribuição por referência na prévia.";
                    continue;
                }
                row.Status = "REVISÃO NECESSÁRIA"; row.Severity = "warning";
                row.Notes = "A rubrica " + group.Key.Code + " aparece em mais de uma publicação para este contracheque. O PDF não permite atribuir o lançamento a cada publicação/referência; não foi reutilizado como confirmação individual.";
            }
        }
    }

    private static List<PaystubRubric> FindRubricsByDescription(PaymentConferenceExpectedItem item, List<PaystubRubric> rubrics)
    {
        var type = Normalize(item.PaymentType);
        var pattern = type switch
        {
            "AUXILIO-ALIMENTACAO" => @"\bAUX(?:ILIO)?\s+ALIM(?:ENTACAO)?\b",
            "AUXILIO-TRANSPORTE" => @"\bAUX(?:ILIO)?\s+TRANSP(?:ORTE)?\b",
            "FERIAS" => @"\bADICIONAL\s+(?:DE\s+)?FERIAS\b",
            "ADICIONAL DE FERIAS — AJUSTE DE CONTAS" => @"\b(?:ADC|ADICIONAL)\s+(?:DE\s+)?FERIAS\s+(?:AJ|AJUSTE)\b",
            "INDENIZACAO DE FERIAS" => @"\bINDENIZACAO\s+(?:DE\s+)?FERIAS\b",
            "GRATIFICACAO DE REPRESENTACAO" => @"\bGRAT(?:IFICACAO)?\s+(?:DE\s+)?REP(?:RESENT|RESENTACAO)?\b",
            "ADICIONAL HABILITACAO" => @"\bADIC(?:IONAL)?\s+(?:DE\s+)?HABILIT(?:ACAO)?\b",
            "AUXILIO-FARDAMENTO" => @"\bAUX(?:ILIO)?\s+FARD(?:AMENTO)?\b",
            "ADICIONAL NATALINO" => @"\bADIC(?:IONAL)?\s+NATAL(?:INO)?\b",
            _ => ""
        };
        if (pattern.Length == 0) return [];
        var debit = item.ExpectedCodes.Any(c => c.StartsWith("DR") || c.StartsWith("ND") || c.StartsWith("AD") || c.StartsWith("ED"));
        // A repayment or deduction must never be mistaken for receipt of the benefit (or vice versa).
        return rubrics.Where(r => (debit ? Regex.IsMatch(r.Code, @"^(DR|ND|AD|ED)") : Regex.IsMatch(r.Code, @"^(NR|AR|ER|FR)"))
            && Regex.IsMatch(Normalize(r.Description).Replace("-", " "), pattern)).ToList();
    }
}
