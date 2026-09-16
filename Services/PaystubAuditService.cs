using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Auditoria de contracheques integralmente em C#.
/// Localiza o PDF, extrai o texto com PdfPig e interpreta rubricas/rodapé sem Python.
/// </summary>
public sealed partial class PaystubAuditService
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    public const string ParserVersion = "2026-09-06-pdf-vacation-only-v10";
    private readonly PaystubService _paystubs;
    private readonly PdfTextService _pdf;
    private readonly LogService _log;
    private Dictionary<string,List<string>>? _identifiedFiles;
    private (int Year,int Month) _preparedPeriod;
    public async Task PrepareFolderAsync(string folder,int year,int month,CancellationToken token=default)
    {
        var index = new Dictionary<string,List<string>>();
        if(!Directory.Exists(folder))throw new DirectoryNotFoundException("Pasta dos contracheques não encontrada: "+folder);
        foreach(var path in Directory.EnumerateFiles(folder,"*.pdf",SearchOption.AllDirectories).Where(p=>!Regex.IsMatch(Normalize(Path.GetFileName(p)),@"FICHA|ESPELHO")))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var descriptor=PaymentConferenceService.PayrollDescriptor(await _pdf.ExtractFirstPageAsync(path,token));
                if(descriptor is not {} d || d.Month!=month || d.Year!=year)continue;
                if(!index.TryGetValue(d.Cpf,out var paths))index[d.Cpf]=paths=[];paths.Add(path);
            }
            catch(OperationCanceledException){throw;}
            catch(Exception ex){await _log.WriteAsync("Cabeçalho de contracheque não identificado: "+Path.GetFileName(path),ex);}
        }
        _identifiedFiles=index;_preparedPeriod=(year,month);
    }

    public PaystubAuditService(PaystubService paystubs, PdfTextService pdf, LogService log)
    {
        _paystubs = paystubs;
        _pdf = pdf;
        _log = log;
    }

    public async Task<PaystubRemunerationSnapshot> ReadRemunerationAsync(
        MilitaryRecord military, string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new PaystubRemunerationSnapshot { Error = "Arquivo do contracheque não encontrado." };
        try
        {
            var parsed = Parse(await _pdf.ExtractAsync(path, cancellationToken), military);
            if (parsed.Salary <= 0)
                return new PaystubRemunerationSnapshot { Error = "A rubrica de soldo não foi identificada no contracheque." };
            return new PaystubRemunerationSnapshot
            {
                Success = true,
                Salary = (decimal)parsed.Salary,
                QualificationAdditional = (decimal)parsed.QualificationAdditional,
                QualificationPercent = parsed.QualificationPercent is { } qualificationPercent ? (decimal)qualificationPercent : null,
                MilitaryAdditional = (decimal)parsed.MilitaryAdditional,
                AvailabilityAdditional = (decimal)parsed.AvailabilityCompensation,
                PermanenceAdditional = (decimal)parsed.PermanenceAdditional,
                MilitaryAdditionalPercent = parsed.MilitaryAdditionalPercent is { } militaryPercent ? (decimal)militaryPercent : null,
                AvailabilityPercent = parsed.AvailabilityPercent is { } availabilityPercent ? (decimal)availabilityPercent : null,
                PermanencePercent = parsed.PermanencePercent is { } permanencePercent ? (decimal)permanencePercent : null,
                PreSchool = (decimal)parsed.PreSchool,
                FamilySalary = (decimal)parsed.FamilySalary,
                Fusex = (decimal)parsed.Fusex,
                FusexPercent = parsed.FusexPercent is { } fusexPercent ? (decimal)fusexPercent : null,
                MilitaryPension = (decimal)parsed.MilitaryPension,
                MilitaryPension105Percent = parsed.MilitaryPension105Percent is { } pension105 ? (decimal)pension105 : null,
                MilitaryPension15Percent = parsed.MilitaryPension15Percent is { } pension15 ? (decimal)pension15 : null,
                FusexDependent = (decimal)parsed.DependentFusex,
                FusexMedical = (decimal)parsed.MedicalFusex,
                PnrDiscount = (decimal)parsed.Pnr,
                PnrPercent = parsed.PnrPercent is { } pnrPercent ? (decimal)pnrPercent : null,
                ExistingAlimony = (decimal)parsed.Alimony,
                IncomeTax = (decimal)parsed.Irrf
            };
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"Falha ao ler remuneração do contracheque de {military.Name}.", ex);
            return new PaystubRemunerationSnapshot { Error = "Não foi possível interpretar o contracheque: " + ex.Message };
        }
    }

    public async Task<PaystubAuditRow> AuditAsync(
        MilitaryRecord military,
        int year,
        int month,
        string? preferredDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string? path;
        if(_identifiedFiles is not null && _preparedPeriod==(year,month))
        {
            var files=_identifiedFiles.GetValueOrDefault(MilitaryFormatting.Digits(military.Cpf))??[];
            if(files.Count>1)
            {
                var versions=files.GroupBy(p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToList();
                if(versions.Count>1)return Empty(military,"Mais de uma versão de contracheque para o CPF e competência. Conferir qual é a válida.");
            }
            path=files.FirstOrDefault();
        }
        else path = await _paystubs.FindBestInDirectoryAsync(military, month, year, preferredDirectory, cancellationToken);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Empty(military, "Contracheque não salvo para esta competência.");

        try
        {
            var text = await _pdf.ExtractAsync(path, cancellationToken);
            var headerError = PaymentConferenceService.ValidatePayrollIdentity(text, military.Cpf, month, year);
            if (headerError.Length > 0) return new PaystubAuditRow
            {
                MilitaryId = military.Id, Rank = military.Rank, Name = military.Name, Cpf = military.Cpf,
                Military = military, PdfPath = path, Pdf = Path.GetFileName(path), PdfOk = true,
                Year = year, Month = month, Situation = "Verificar PDF: " + headerError
            };
            var parsed = Parse(text, military);
            return new PaystubAuditRow
            {
                Year = year, Month = month, HeaderValidated = true, PdfModifiedTicks = File.GetLastWriteTimeUtc(path).Ticks,
                SalaryPdf = parsed.Salary,
                TransportShare = Math.Round(parsed.Salary * 0.06 * ((military.TransportWorkingDays ?? 22) / 30d), 2),
                TransportCalculatedNet = military.TransportGrossTotal > 0 ? Math.Round(Math.Max(0, military.TransportGrossTotal.Value - parsed.Salary * 0.06 * ((military.TransportWorkingDays ?? 22) / 30d)), 2) : null,
                MilitaryId = military.Id,
                Rank = military.Rank,
                Name = military.Name,
                WarName = military.WarName,
                Cpf = military.Cpf,
                Idt = military.MilitaryId,
                Prec = military.PrecCp,
                Pdf = Path.GetFileName(path),
                PdfPath = path,
                PdfOk = true,
                BankStatus = parsed.BankStatus,
                BankPdf = parsed.BankPdf,
                AgencyPdf = parsed.AgencyPdf,
                AccountPdf = parsed.AccountPdf,
                BankDatabase = military.Bank,
                AgencyDatabase = military.Agency,
                AccountDatabase = military.Account,
                BankDivergent = parsed.BankDivergent,
                PaymentStatus = parsed.PaymentStatus,
                PaymentDifferent = parsed.PaymentDifferent,
                AuxPdf = parsed.AuxPdf,
                AuxDatabase = parsed.AuxDatabase,
                AuxDifference = parsed.AuxDifference,
                AuxStatus = parsed.AuxStatus,
                AuxDr = parsed.AuxDr,
                AuxAr = parsed.AuxAr,
                Fusex = parsed.Fusex,
                HasAnyFusexRubric = parsed.HasAnyFusexRubric,
                DependentFusex = parsed.DependentFusex,
                MedicalFusex = parsed.MedicalFusex,
                FamilySalary = parsed.FamilySalary,
                Dependents = parsed.Dependents,
                PreSchool = parsed.PreSchool,
                Vacation = parsed.Vacation,
                FoodAid = parsed.FoodAid,
                Differences = parsed.Differences,
                Pension = parsed.Pension,
                MilitaryPension = parsed.MilitaryPension,
                Alimony = parsed.Alimony,
                Irrf = parsed.Irrf,
                QualificationAdditional = parsed.QualificationAdditional,
                AvailabilityCompensation = parsed.AvailabilityCompensation,
                Pnr = parsed.Pnr,
                Loans = parsed.Loans,
                Revenue = parsed.Revenue,
                Expense = parsed.Expense,
                Net = parsed.Net,
                Situation = parsed.Situation,
                Lines = parsed.Lines,
                Military = military
            };
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"Falha na auditoria nativa do contracheque de {military.Name}.", ex);
            return new PaystubAuditRow
            {
                MilitaryId = military.Id, Rank = military.Rank, Name = military.Name, WarName = military.WarName,
                Cpf = military.Cpf, Idt = military.MilitaryId, Prec = military.PrecCp,
                Pdf = Path.GetFileName(path), PdfPath = path, PdfOk = true,
                BankDatabase = military.Bank, AgencyDatabase = military.Agency, AccountDatabase = military.Account,
                Situation = "Erro ao ler contracheque: " + ex.Message, Military = military
            };
        }
    }

    private static PaystubAuditRow Empty(MilitaryRecord military, string situation) => new()
    {
        MilitaryId = military.Id, Rank = military.Rank, Name = military.Name, WarName = military.WarName,
        Cpf = military.Cpf, Idt = military.MilitaryId, Prec = military.PrecCp,
        Pdf = "NÃO ENCONTRADO", PdfOk = false,
        BankDatabase = military.Bank, AgencyDatabase = military.Agency, AccountDatabase = military.Account,
        Situation = situation, Military = military
    };

    private static ParsedAudit Parse(string text, MilitaryRecord military)
    {
        var rubrics = ReadRubrics(text);

        var auxKeywords = new[] { "AUX TRANSP", "AUXILIO TRANSP", "AUXILIO TRANSPORTE", "TRANSPORTE" };
        var (auxNormal, auxLines) = Sum(rubrics, codes: ["NR0095"], keywords: auxKeywords, excludes: ["DESCONTO", "COTA", "BASE"]);
        var (auxDr, auxDrLines) = Sum(rubrics, prefixes: ["D", "DR"], keywords: auxKeywords);
        var (auxAr, auxArLines) = Sum(rubrics, prefixes: ["A", "AR"], keywords: auxKeywords);
        if (auxNormal <= 0 && auxDr <= 0 && auxAr <= 0)
            (auxNormal, auxLines) = Sum(rubrics, prefixes: ["NR"], keywords: auxKeywords, excludes: ["BASE", "COMPROVANTE", "COTA"]);
        var auxLowConfidence = MatchedRubrics(rubrics, codes: ["NR0095"], keywords: auxKeywords).Any(x => x.LowConfidence);

        var fusexKeywords = new[] { "FUSEX", "FUS EX", "FUNDO DE SAUDE", "FUNDO SAUDE" };
        var (fusex, fusexLines) = Sum(rubrics, codes: ["ND0001"], keywords: fusexKeywords, excludes: ["DEPENDENTE", "DESPESA MEDICA", "DESPESA MÉDICA"]);
        if (fusex <= 0) (fusex, fusexLines) = Sum(rubrics, keywords: ["FUSEX 3%", "FUS EX 3", "FUNDO DE SAUDE 3"], excludes: ["DEPENDENTE", "DESPESA MEDICA", "DESPESA MÉDICA"]);
        var (depFusex, depFusexLines) = Sum(rubrics, codes: ["ND0011"], keywords: fusexKeywords);
        if (depFusex <= 0) (depFusex, depFusexLines) = Sum(rubrics, keywords: ["DESCONTO DEPENDENTE", "DEPENDENTE FUSEX", "DEPENDENTE FUS EX"]);
        var (medFusex, medFusexLines) = Sum(rubrics, codes: ["ND0013"], keywords: fusexKeywords);
        if (medFusex <= 0) (medFusex, medFusexLines) = Sum(rubrics, keywords: ["DESPESA MEDICA", "DESPESA MÉDICA", "FUSEX MEDICA"]);
        var fusexAnyLines = MatchedRubrics(rubrics, keywords: fusexKeywords).Select(x => x.Line).Distinct().ToList();
        var hasAnyFusexRubric = fusex > 0 || depFusex > 0 || medFusex > 0 || fusexAnyLines.Count > 0;

        var (family, familyLines) = Sum(rubrics, codes: ["NR0018"], keywords: ["SALARIO FAMILIA"]);
        if (family <= 0) (family, familyLines) = Sum(rubrics, keywords: ["SALARIO FAMILIA", "SAL FAMILIA"]);
        var (preSchool, preSchoolLines) = Sum(rubrics, codes: ["NR0077"], keywords: ["PRE ESCOLAR", "ASSISTENCIA PRE"]);
        if (preSchool <= 0) (preSchool, preSchoolLines) = Sum(rubrics, keywords: ["PRE ESCOLAR", "ASSISTENCIA PRE"]);
        var (vacation, vacationLines) = Sum(rubrics, prefixes: ["NR", "AR", "ER", "FR"], keywords: ["FERIAS"], excludes: ["DESCONTO", "DEVOLUCAO", "RESTITUICAO"]);
        var (food, foodLines) = Sum(rubrics, keywords: ["AUX ALIMENT", "AUXILIO ALIMENT", "ALIMENTACAO"]);
        var (drAr, drArLines) = Sum(rubrics, prefixes: ["DR", "AR", "ER", "FR", "DD"]);
        var (arrearsKw, arrearsKwLines) = Sum(rubrics, keywords: ["ATRAS", "EXERCICIO ANTERIOR", "DIFERENCA"]);
        var differences = Math.Max(drAr, arrearsKw);
        var differenceLines = drArLines.Concat(arrearsKwLines).Distinct().Take(10).ToList();
        var (milPension, milPensionLines) = Sum(rubrics, keywords: ["PENSAO MILITAR"]);
        var militaryPensionPercents = MatchedRubrics(rubrics, keywords: ["PENSAO MILITAR"])
            .Where(row => row.Percent.HasValue).Select(row => row.Percent!.Value).ToList();
        var militaryPension105Percent = militaryPensionPercents.FirstOrDefault(value => Math.Abs(value - 10.5d) <= 0.02d);
        var militaryPension15Percent = militaryPensionPercents.FirstOrDefault(value => Math.Abs(value - 1.5d) <= 0.02d);
        var (alimony, alimonyLines) = Sum(rubrics, keywords: ["PENSAO ALIMENTICIA", "PENSAO ALIMENT"]);
        var (irrf, irrfLines) = Sum(rubrics, keywords: ["IRRF", "IMPOSTO DE RENDA"]);
        var (salary, salaryLines) = Sum(rubrics, codes: ["NR0001"], keywords: ["SOLDO"]);
        // No contracheque do CPEx a habilitação é a rubrica NR0003. O código é
        // mais estável que a descrição, que pode vir abreviada ou quebrada pelo PDF.
        var (qualification, qualificationLines) = Sum(rubrics, codes: ["NR0003"]);
        if (qualification <= 0)
            (qualification, qualificationLines) = Sum(rubrics, keywords: ["ADICIONAL DE HABILITACAO", "ADIC HABILIT", "AD HABILIT", "HABILITACAO"]);
        var qualificationPercent = FirstPercent(rubrics, codes: ["NR0003"])
                                   ?? FirstPercent(rubrics, keywords: ["ADICIONAL DE HABILITACAO", "ADIC HABILIT", "AD HABILIT", "HABILITACAO"]);
        var (militaryAdditional, militaryAdditionalLines) = Sum(rubrics, codes: ["NR0014"], keywords: ["ADICIONAL MILITAR", "ADIC MILITAR"]);
        var (availability, availabilityLines) = Sum(rubrics, codes: ["NR0170"], keywords: ["AD C DISP MIL", "ADICIONAL DE COMPENSACAO"]);
        var militaryAdditionalPercent = FirstPercent(rubrics, codes: ["NR0014"], keywords: ["ADICIONAL MILITAR", "ADIC MILITAR"]);
        var availabilityPercent = FirstPercent(rubrics, codes: ["NR0170"], keywords: ["AD C DISP MIL", "ADICIONAL DE COMPENSACAO"]);
        var (permanence, permanenceLines) = Sum(rubrics, keywords: ["ADICIONAL DE PERMANENCIA", "ADIC PERMANENCIA"]);
        var permanencePercent = FirstPercent(rubrics, keywords: ["ADICIONAL DE PERMANENCIA", "ADIC PERMANENCIA"]);
        var fusexPercent = FirstPercent(rubrics, codes: ["ND0001"], keywords: fusexKeywords);
        var (pnr, pnrLines) = Sum(rubrics, keywords: ["PNR", "OCUPACAO PNR"]);
        var pnrPercent = FirstPercent(rubrics, keywords: ["PNR", "OCUPACAO PNR"]);
        var (loans, loanLines) = Sum(rubrics, keywords: ["FHE", "FIN IMOB", "EMPREST", "SABEMI", "SEGURO", "CSSE", "PEC"]);

        // O rodapé do CPEx é uma tabela independente das rubricas. Em muitos
        // PDFs, os rótulos ficam em uma linha e os valores na seguinte; por isso a
        // leitura isolada por rótulo perdia a situação de pagamento no último campo.
        var footer = ReadFooter(text);
        var revenue = footer.Revenue;
        var expense = footer.Expense;
        var net = footer.Net;
        var bankPdf = footer.Bank;
        var agencyPdf = footer.Agency;
        var accountPdf = footer.Account;
        var paymentStatus = CanonicalPaymentStatus(footer.PaymentStatus);

        var bankDivergent = IsDifferentBank(bankPdf, military.Bank)
                            || IsDifferentNumber(agencyPdf, military.Agency)
                            || IsDifferentNumber(accountPdf, military.Account);
        var bankStatus = bankDivergent ? "DIVERGENTE" : "OK";
        if (string.IsNullOrWhiteSpace(bankPdf) && string.IsNullOrWhiteSpace(agencyPdf) && string.IsNullOrWhiteSpace(accountPdf))
            bankStatus = "NÃO IDENTIFICADO NO PDF";
        var paymentDifferent = !IsNormalPaymentStatus(paymentStatus);

        var auxDb = ParseMoney(military.TransportAidValue);
        double? expectedAux = MilitaryRecord.IsYes(military.ReceivesTransportAid) || auxDb > 0 ? auxDb : null;
        double? auxDiff = expectedAux.HasValue ? auxNormal - expectedAux.Value : null;
        var auxStatus = expectedAux.HasValue
            ? auxNormal > 0 ? auxLowConfidence ? "VERIFICAR MANUALMENTE" : Math.Abs(auxDiff!.Value) <= 0.05 ? "OK" : "DIVERGENTE" : "NÃO RECEBEU"
            : auxNormal > 0 ? "RECEBE NO CONTRACHEQUE" : auxDr > 0 ? "SÓ DESCONTO DR" : "—";
        if (vacation > 0 && auxNormal <= 0) auxStatus = "OK · FÉRIAS PAGAS, SEM AT";
        if (auxDr > 0) auxStatus += " + DESC DR";

        var dependents = family > 0 ? Math.Max(1, (int)Math.Round(family / 0.16d)) : 0;
        var alerts = new List<string>();
        if (auxStatus.Contains("DIVERG", StringComparison.OrdinalIgnoreCase)) alerts.Add("Auxílio-transporte diferente");
        if (auxStatus.Contains("NÃO RECEBEU", StringComparison.OrdinalIgnoreCase)) alerts.Add("Auxílio-transporte previsto, mas não localizado");
        if (auxDr > 0) alerts.Add("Desconto de auxílio-transporte (DR)");
        if (auxAr > 0) alerts.Add("Auxílio-transporte AR");
        if (family > 0) alerts.Add($"Salário-família ({dependents} dep.)");
        if (preSchool > 0) alerts.Add("Assistência pré-escolar");
        if (vacation > 0) alerts.Add("Pagamento de férias no contracheque");
        if (food > 0) alerts.Add("Recebeu auxílio-alimentação");
        if (differences > 0) alerts.Add("DR/AR/atrasados/diferença");
        if (fusex <= 0 && hasAnyFusexRubric) alerts.Add("FUSEx localizado — conferir classificação");
        if (fusex <= 0 && !hasAnyFusexRubric) alerts.Add("Sem FUSEx 3% localizado");
        if (depFusex > 0) alerts.Add("FUSEx dependente");
        if (medFusex > 0) alerts.Add("Despesa médica FUSEx");
        if (alimony > 0) alerts.Add("Pensão alimentícia");
        if (pnr > 0) alerts.Add("PNR");
        if (loans > 0) alerts.Add("Empréstimo/seguro/FHE");
        if (bankDivergent) alerts.Add("Banco/agência/conta diferente do cadastro");
        if (paymentDifferent) alerts.Add(paymentStatus.Equals("NÃO IDENTIFICADA", StringComparison.OrdinalIgnoreCase)
            ? "Situação de pagamento não identificada no rodapé"
            : "Situação de pagamento: " + paymentStatus);

        return new ParsedAudit
        {
            BankPdf = bankPdf, AgencyPdf = agencyPdf, AccountPdf = accountPdf,
            BankDivergent = bankDivergent, BankStatus = bankStatus,
            PaymentStatus = paymentStatus, PaymentDifferent = paymentDifferent,
            AuxPdf = auxNormal, AuxDatabase = expectedAux, AuxDifference = auxDiff, AuxStatus = auxStatus,
            AuxDr = auxDr, AuxAr = auxAr, Fusex = fusex, HasAnyFusexRubric = hasAnyFusexRubric, DependentFusex = depFusex, MedicalFusex = medFusex,
            FamilySalary = family, Dependents = dependents, PreSchool = preSchool, Vacation = vacation,
            FoodAid = food, Differences = differences, MilitaryPension = milPension, Alimony = alimony,
            Pension = milPension + alimony, Irrf = irrf, Salary = salary, QualificationAdditional = qualification,
            QualificationPercent = qualificationPercent,
            MilitaryAdditional = militaryAdditional,
            AvailabilityCompensation = availability,
            PermanenceAdditional = permanence,
            MilitaryAdditionalPercent = militaryAdditionalPercent,
            AvailabilityPercent = availabilityPercent,
            PermanencePercent = permanencePercent,
            FusexPercent = fusexPercent,
            MilitaryPension105Percent = militaryPension105Percent > 0d ? militaryPension105Percent : null,
            MilitaryPension15Percent = militaryPension15Percent > 0d ? militaryPension15Percent : null,
            Pnr = pnr, Loans = loans,
            PnrPercent = pnrPercent,
            Revenue = revenue, Expense = expense, Net = net,
            Situation = alerts.Count == 0 ? "Sem achado relevante" : string.Join("; ", alerts),
            Lines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["aux_transporte"] = auxLines.Take(8).ToList(), ["aux_dr"] = auxDrLines.Take(8).ToList(),
                ["aux_ar"] = auxArLines.Take(8).ToList(), ["fusex"] = (fusexLines.Count > 0 ? fusexLines : fusexAnyLines).Take(5).ToList(),
                ["dep_fusex"] = depFusexLines.Take(5).ToList(), ["med_fusex"] = medFusexLines.Take(5).ToList(),
                ["salario_familia"] = familyLines.Take(5).ToList(), ["pre_escolar"] = preSchoolLines.Take(5).ToList(),
                ["ferias"] = vacationLines.Take(5).ToList(), ["aux_alimentacao"] = foodLines.Take(5).ToList(),
                ["atrasados"] = differenceLines, ["pensao_militar"] = milPensionLines.Take(5).ToList(),
                ["pensao_alimenticia"] = alimonyLines.Take(5).ToList(), ["irrf"] = irrfLines.Take(5).ToList(),
                ["soldo"] = salaryLines.Take(5).ToList(), ["habilitacao"] = qualificationLines.Take(5).ToList(),
                ["adicional_militar"] = militaryAdditionalLines.Take(5).ToList(), ["ad_c_disp_mil"] = availabilityLines.Take(5).ToList(),
                ["adicional_permanencia"] = permanenceLines.Take(5).ToList(),
                ["pnr"] = pnrLines.Take(5).ToList(), ["emprestimos"] = loanLines.Take(5).ToList(),
                ["rodape_pagamento"] =
                [
                    $"Situação: {paymentStatus} | Banco: {bankPdf} | Agência: {agencyPdf} | Conta: {accountPdf}",
                    $"Receita: {MilitaryFormatting.FormatMoney(revenue)} | Despesa: {MilitaryFormatting.FormatMoney(expense)} | Líquido: {MilitaryFormatting.FormatMoney(net)}"
                ]
            }
        };
    }

    private static List<Rubric> ReadRubrics(string text)
    {
        var lines = (text ?? string.Empty).Split('\n').Select(OneLine).Where(x => x.Length > 0).ToList();
        var result = new List<Rubric>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string code, string block)
        {
            block = OneLine(block);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(block)) return;
            var moneyTexts = MoneyRegex().Matches(RemoveAccessStampNoise(block)).Select(x => x.Value).ToList();
            var moneyValues = moneyTexts.Select(ParseMoney).ToList();
            var money = moneyValues.Count == 0 ? 0 : moneyValues[0];
            var key = code + "|" + Normalize(block)[..Math.Min(120, Normalize(block).Length)];
            if (money <= 0 || !seen.Add(key)) return;
            var normalized = Normalize(block);
            var lowConfidence = moneyValues.Count > 1 && money < moneyValues.Max() * 0.5d
                && !Regex.IsMatch(Normalize(text), @"VALOR\s+%\s+R\s*/\s*D");
            var description = DescriptionFromBlock(code, block);
            var evidence = $"{code.ToUpperInvariant()} - {description} = {MilitaryFormatting.FormatMoney(money)} | valores: {string.Join(", ", moneyValues.Select(MilitaryFormatting.FormatMoney))} | evidência: {block}";
            if (lowConfidence) evidence = "CONFIANÇA BAIXA - " + evidence;
            result.Add(new Rubric(code.ToUpperInvariant(), evidence, normalized, Math.Abs(money), RubricPercent(block), lowConfidence));
        }

        // 1) Layout por linha, quando o PDF veio bem extraído.
        for (var i = 0; i < lines.Count; i++)
        {
            var normalized = Normalize(lines[i]);
            var match = Regex.Match(normalized, @"\b([A-Z]{1,3}\d{3,5})\b");
            if (!match.Success) continue;
            var parts = new List<string> { lines[i] };
            for (var j = i + 1; j < lines.Count && j <= i + 5; j++)
            {
                var next = Normalize(lines[j]);
                if (Regex.IsMatch(next, @"\b[A-Z]{1,3}\d{3,5}\b") || IsFooter(next)) break;
                parts.Add(lines[j]);
            }
            Add(match.Groups[1].Value, string.Join(" ", parts));
        }

        // 2) Fallback por texto contínuo. Alguns PDFs juntam colunas/linhas e o código não
        // fica no início da linha; este regex captura do código até o próximo código/rodapé.
        var normalizedText = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        foreach (Match match in Regex.Matches(normalizedText,
                     @"\b(?<code>[A-Z]{1,3}\d{3,5})\b\s*(?<body>.{0,240}?)(?=\b[A-Z]{1,3}\d{3,5}\b|\bRECEITA\b|\bDESPESA\b|\bL[IÍ]QUIDO\b|$)",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var code = match.Groups["code"].Value;
            var block = (code + " " + match.Groups["body"].Value).Trim();
            if (MoneyRegex().IsMatch(block)) Add(code, block);
        }

        return result;
    }

    private static bool IsFooter(string value) => new[] { "DATA IMP", "DEP IR", "ISENTO IR", "RECEITA", "DESPESA", "LIQUIDO", "BANCO", "AGENCIA", "C/C", "SITUACAO" }.Any(value.StartsWith);

    private static (double Total, List<string> Lines) Sum(
        IEnumerable<Rubric> rubrics,
        string[]? codes = null,
        string[]? prefixes = null,
        string[]? keywords = null,
        string[]? excludes = null)
    {
        var codeSet = codes?.Select(x => x.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keywordSet = keywords?.Select(Normalize).ToArray() ?? [];
        var excludeSet = excludes?.Select(Normalize).ToArray() ?? [];
        var matched = MatchedRubrics(rubrics, codes, prefixes, keywords, excludes)
            .Where(r => r.Value > 0)
            .ToList();
        return (matched.Sum(x => x.Value), matched.Select(x => x.Line).Distinct().ToList());
    }

    private static List<Rubric> MatchedRubrics(
        IEnumerable<Rubric> rubrics,
        string[]? codes = null,
        string[]? prefixes = null,
        string[]? keywords = null,
        string[]? excludes = null)
    {
        var codeSet = codes?.Select(x => x.ToUpperInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keywordSet = keywords?.Select(Normalize).ToArray() ?? [];
        var excludeSet = excludes?.Select(Normalize).ToArray() ?? [];
        return rubrics.Where(r =>
                (codeSet is null || codeSet.Contains(r.Code))
                && (prefixes is null || prefixes.Any(p => r.Code.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                && (keywordSet.Length == 0 || keywordSet.Any(r.Normalized.Contains))
                && (excludeSet.Length == 0 || !excludeSet.Any(r.Normalized.Contains)))
            .ToList();
    }

    private static double? FirstPercent(
        IEnumerable<Rubric> rubrics,
        string[]? codes = null,
        string[]? keywords = null)
        => MatchedRubrics(rubrics, codes: codes, keywords: keywords)
            .Select(row => row.Percent)
            .FirstOrDefault(percent => percent.HasValue);

    public static decimal? ReadRubricPercentage(string text, string rubricCode)
    {
        var percent = FirstPercent(ReadRubrics(text), codes: [rubricCode]);
        return percent is { } value ? (decimal)value : null;
    }

    private static double? RubricPercent(string block)
    {
        var clean = RemoveAccessStampNoise(OneLine(block));
        var match = Regex.Match(clean,
            @"(?:R\$\s*)?[-+]?\d{1,3}(?:\.\d{3})*,\d{2}\s+(?<percent>\d{1,3},\d{2})\s+[RD]\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var percent = ParseMoney(match.Groups["percent"].Value);
        return percent is >= 0d and <= 100d ? percent : null;
    }

    private static string DescriptionFromBlock(string code, string block)
    {
        var text = Regex.Replace(block ?? string.Empty, "^" + Regex.Escape(code) + @"\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        text = MoneyRegex().Replace(text, string.Empty).Trim();
        text = Regex.Replace(text, @"\s+\d{1,3},\d{2}\s+[RD]\s+[-+]?.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(text) ? block ?? string.Empty : text;
    }

    private static FooterData ReadFooter(string text)
    {
        var lines = (text ?? string.Empty).Split('\n').Select(OneLine).Where(x => x.Length > 0).ToList();
        var normalized = lines.Select(Normalize).ToList();
        var revenue = 0d;
        var expense = 0d;
        var net = 0d;
        var bank = string.Empty;
        var agency = string.Empty;
        var account = string.Empty;
        var paymentStatus = string.Empty;

        for (var i = 0; i < normalized.Count; i++)
        {
            var line = normalized[i];

            if (line.Contains("RECEITA", StringComparison.Ordinal)
                && line.Contains("DESPESA", StringComparison.Ordinal)
                && line.Contains("LIQUIDO", StringComparison.Ordinal))
            {
                revenue = ExtractMoneyAfterLabel(line, "RECEITA");
                expense = ExtractMoneyAfterLabel(line, "DESPESA");
                net = ExtractMoneyAfterLabel(line, "LIQUIDO");

                if (revenue <= 0 || expense <= 0 || net <= 0)
                {
                    var valueBlock = string.Join(" ", normalized.Skip(i + 1).Take(2));
                    var values = MoneyRegex().Matches(valueBlock).Select(x => ParseMoney(x.Value)).ToList();
                    if (values.Count >= 3)
                    {
                        revenue = values[^3];
                        expense = values[^2];
                        net = values[^1];
                    }
                }
            }

            if (line.Contains("BANCO", StringComparison.Ordinal)
                && line.Contains("AGENCIA", StringComparison.Ordinal)
                && (line.Contains("C/C", StringComparison.Ordinal) || line.Contains("CONTA", StringComparison.Ordinal)))
            {
                bank = ExtractTextAfterLabel(line, "BANCO");
                agency = ExtractTextAfterLabel(line, "AGENCIA");
                account = ExtractTextAfterLabel(line, line.Contains("C/C", StringComparison.Ordinal) ? "C/C" : "CONTA");

                var valueLines = normalized.Skip(i + 1).Take(3).ToList();
                var valueBlock = string.Join(" ", valueLines);
                // Em alguns arquivos o CPEx extrai "IDT MARGEM:" em uma linha
                // isolada antes dos valores bancários. Os três primeiros blocos
                // numéricos do trecho continuam sendo banco, agência e conta.
                var bankValueLine = valueLines.FirstOrDefault(x => Regex.Matches(x, @"\d+").Count >= 3) ?? valueBlock;
                var numberParts = Regex.Matches(bankValueLine, @"\d+").Select(x => x.Value).ToList();
                if (string.IsNullOrWhiteSpace(bank) && numberParts.Count > 0) bank = numberParts[0];
                if (string.IsNullOrWhiteSpace(agency) && numberParts.Count > 1) agency = numberParts[1];
                if (string.IsNullOrWhiteSpace(account) && numberParts.Count > 2) account = numberParts[2];

                paymentStatus = ExtractPaymentStatus(line);
                if (string.IsNullOrWhiteSpace(paymentStatus))
                    paymentStatus = valueLines.Select(ExtractPaymentStatus).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(paymentStatus)) paymentStatus = FindKnownPaymentStatus(valueBlock);
            }

            if (string.IsNullOrWhiteSpace(paymentStatus) && line.Contains("SITUACAO", StringComparison.Ordinal))
            {
                paymentStatus = ExtractPaymentStatus(line);
                if (string.IsNullOrWhiteSpace(paymentStatus))
                {
                    var following = string.Join(" ", normalized.Skip(i + 1).Take(3));
                    paymentStatus = ExtractPaymentStatus(following);
                    if (string.IsNullOrWhiteSpace(paymentStatus)) paymentStatus = FindKnownPaymentStatus(following);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(paymentStatus))
        {
            var footerTail = string.Join(" ", normalized.TakeLast(10));
            paymentStatus = FindKnownPaymentStatus(footerTail);
        }

        return new FooterData(bank, agency, account, paymentStatus, revenue, expense, net);
    }

    private static double ExtractMoneyAfterLabel(string normalizedLine, string label)
    {
        var value = ExtractTextAfterLabel(normalizedLine, label);
        return MoneyRegex().Matches(value).Select(x => ParseMoney(x.Value)).FirstOrDefault();
    }

    private static string ExtractTextAfterLabel(string normalizedLine, string label)
    {
        var escaped = Regex.Escape(Normalize(label).TrimEnd(':'));
        var match = Regex.Match(normalizedLine ?? string.Empty,
            $@"\b{escaped}\s*:\s*(?<value>.*?)(?=\b(?:DATA\s+IMP(?:/PRACA)?|DEP\s+IR|ISENTO\s+IR|RECEITA|DESPESA|LIQUIDO|BANCO|AGENCIA|C/C|CONTA|IDT\s+MARGEM|SITUACAO)\s*:|$)",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static string ExtractPaymentStatus(string normalizedLine)
    {
        if (string.IsNullOrWhiteSpace(normalizedLine)) return string.Empty;
        foreach (var label in new[] { "SITUACAO", "IDT MARGEM" })
        {
            var match = Regex.Match(normalizedLine, $@"\b{Regex.Escape(label)}\s*:\s*(?<value>.+)$", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var value = FindKnownPaymentStatus(match.Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(value)) return value;
            var generic = Regex.Replace(match.Groups["value"].Value, @"^[\d\s./-]+", string.Empty).Trim(' ', ':', '-', '|');
            if (generic.Length is > 1 and <= 80) return generic;
        }
        return string.Empty;
    }

    private static string FindKnownPaymentStatus(string value)
    {
        var normalized = Normalize(value);
        var known = Regex.Match(normalized,
            @"\b(PAGAMENTO\s+SUSPENSO|SUSPENS[OA]|NORMAL|BLOQUEAD[OA]|PAGAMENTO\s+BLOQUEADO|CANCELAD[OA]|EXCLUID[OA]|RETID[OA]|PAGAMENTO\s+RETIDO|SEM\s+PAGAMENTO|NAO\s+PAGO|CESSAD[OA])\b",
            RegexOptions.IgnoreCase);
        return known.Success ? known.Value.Trim() : string.Empty;
    }

    private static string CanonicalPaymentStatus(string value)
    {
        var normalized = Normalize(value).Trim(' ', ':', '-', '|');
        if (string.IsNullOrWhiteSpace(normalized)) return "NÃO IDENTIFICADA";
        if (normalized.Contains("SUSPENS", StringComparison.Ordinal)) return "PAGAMENTO SUSPENSO";
        if (normalized.Equals("NORMAL", StringComparison.Ordinal) || normalized.Equals("PAGAMENTO NORMAL", StringComparison.Ordinal)) return "NORMAL";
        if (normalized.Contains("BLOQUE", StringComparison.Ordinal)) return "PAGAMENTO BLOQUEADO";
        if (normalized.Contains("RETID", StringComparison.Ordinal)) return "PAGAMENTO RETIDO";
        if (normalized.Contains("CANCEL", StringComparison.Ordinal)) return "PAGAMENTO CANCELADO";
        if (normalized.Contains("EXCLUID", StringComparison.Ordinal)) return "PAGAMENTO EXCLUÍDO";
        if (normalized.Contains("SEM PAGAMENTO", StringComparison.Ordinal) || normalized.Contains("NAO PAGO", StringComparison.Ordinal)) return "SEM PAGAMENTO";
        return normalized.Length <= 80 ? normalized : normalized[..80].Trim();
    }

    private static bool IsNormalPaymentStatus(string value)
    {
        var normalized = Normalize(value);
        return normalized.Equals("NORMAL", StringComparison.Ordinal)
               || normalized.Equals("PAGAMENTO NORMAL", StringComparison.Ordinal);
    }

    private static bool IsDifferentBank(string pdf, string database)
    {
        if (string.IsNullOrWhiteSpace(pdf) || string.IsNullOrWhiteSpace(database)) return false;
        var a = BankCode(pdf); var b = BankCode(database);
        return a.Length > 0 && b.Length > 0 && !a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDifferentNumber(string pdf, string database)
    {
        if (string.IsNullOrWhiteSpace(pdf) || string.IsNullOrWhiteSpace(database)) return false;
        var a = Digits(pdf).TrimStart('0'); var b = Digits(database).TrimStart('0');
        return a.Length > 0 && b.Length > 0 && !a.Equals(b, StringComparison.Ordinal);
    }

    private static string BankCode(string value)
    {
        var digits = Regex.Match(value ?? string.Empty, @"(?<!\d)\d{3}(?!\d)").Value;
        if (digits.Length == 3) return digits;
        var normalized = Normalize(value);
        if (normalized.Contains("BANCO DO BRASIL")) return "001";
        if (normalized.Contains("ITAU")) return "341";
        if (normalized.Contains("SANTANDER")) return "033";
        if (normalized.Contains("BRADESCO")) return "237";
        if (normalized.Contains("CAIXA") || normalized.Contains("CEF")) return "104";
        return Digits(value).PadLeft(3, '0');
    }

    private static double ParseMoney(string? value)
    {
        var text = (value ?? string.Empty).Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (text.Length == 0) return 0;
        if (text.Contains(',')) text = text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? Math.Abs(number) : 0;
    }

    private static string Digits(string? value) => Regex.Replace(value ?? string.Empty, @"\D+", string.Empty);
    private static string OneLine(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static string RemoveAccessStampNoise(string value)
        => Regex.Replace(value ?? string.Empty, @"E?\s*m\s*:\s*\d{1,2}/\d{1,2}/\d{4}\s+\d{1,2}:\d{2}:\d{2}", string.Empty, RegexOptions.IgnoreCase);
    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToUpperInvariant(c));
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"(?<!\d)(?:R\$\s*)?[-+]?\d{1,3}(?:\.\d{3})*,\d{2}|(?<!\d)(?:R\$\s*)?[-+]?\d+,\d{2}(?!\d)", RegexOptions.Compiled)]
    private static partial Regex MoneyRegex();

    private sealed record Rubric(string Code, string Line, string Normalized, double Value, double? Percent, bool LowConfidence);
    private sealed record FooterData(string Bank, string Agency, string Account, string PaymentStatus, double Revenue, double Expense, double Net);

    private sealed class ParsedAudit
    {
        public string BankPdf { get; init; } = string.Empty;
        public string AgencyPdf { get; init; } = string.Empty;
        public string AccountPdf { get; init; } = string.Empty;
        public bool BankDivergent { get; init; }
        public string BankStatus { get; init; } = string.Empty;
        public string PaymentStatus { get; init; } = string.Empty;
        public bool PaymentDifferent { get; init; }
        public double AuxPdf { get; init; }
        public double? AuxDatabase { get; init; }
        public double? AuxDifference { get; init; }
        public string AuxStatus { get; init; } = string.Empty;
        public double AuxDr { get; init; }
        public double AuxAr { get; init; }
        public double Fusex { get; init; }
        public bool HasAnyFusexRubric { get; init; }
        public double DependentFusex { get; init; }
        public double MedicalFusex { get; init; }
        public double FamilySalary { get; init; }
        public int Dependents { get; init; }
        public double PreSchool { get; init; }
        public double Vacation { get; init; }
        public double FoodAid { get; init; }
        public double Differences { get; init; }
        public double Pension { get; init; }
        public double MilitaryPension { get; init; }
        public double Alimony { get; init; }
        public double Irrf { get; init; }
        public double QualificationAdditional { get; init; }
        public double? QualificationPercent { get; init; }
        public double Salary { get; init; }
        public double MilitaryAdditional { get; init; }
        public double AvailabilityCompensation { get; init; }
        public double PermanenceAdditional { get; init; }
        public double? MilitaryAdditionalPercent { get; init; }
        public double? AvailabilityPercent { get; init; }
        public double? PermanencePercent { get; init; }
        public double? FusexPercent { get; init; }
        public double? MilitaryPension105Percent { get; init; }
        public double? MilitaryPension15Percent { get; init; }
        public double Pnr { get; init; }
        public double? PnrPercent { get; init; }
        public double Loans { get; init; }
        public double Revenue { get; init; }
        public double Expense { get; init; }
        public double Net { get; init; }
        public string Situation { get; init; } = string.Empty;
        public Dictionary<string, List<string>> Lines { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
