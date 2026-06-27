using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Conferência profissional entre Aditamentos do Furriel e contracheques CPEx/SIPPES.
/// Lê as publicações de pagamento, identifica militares/CPF/PREC, interpreta a natureza
/// da rubrica (N normal, A atrasado, D desconto/anulação) e compara com os PDFs do mês.
/// </summary>
public sealed partial class PaymentConferenceService
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly PdfTextService _pdfText;
    private readonly MilitaryRepository _repository;
    private readonly PaystubService _paystubs;
    private readonly LogService _log;

    public PaymentConferenceService(AppPaths paths, JsonFileService json, PdfTextService pdfText, MilitaryRepository repository, PaystubService paystubs, LogService log)
    {
        _paths = paths;
        _json = json;
        _pdfText = pdfText;
        _repository = repository;
        _paystubs = paystubs;
        _log = log;
        Directory.CreateDirectory(ModuleDirectory);
        Directory.CreateDirectory(ReportsDirectory);
    }

    public string ModuleDirectory => Path.Combine(_paths.DataDirectory, "conferencia_pagamento");
    public string ReportsDirectory => Path.Combine(ModuleDirectory, "relatorios");
    public string SettingsFile => Path.Combine(ModuleDirectory, "config.json");
    public string LastCsvFile => Path.Combine(ReportsDirectory, $"conferencia_pagamento_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

    public async Task<PaymentConferenceSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
        => await _json.LoadAsync<PaymentConferenceSettings>(SettingsFile) ?? new PaymentConferenceSettings { PaystubFolder = _paths.PaystubsDirectory };

    public Task SaveSettingsAsync(PaymentConferenceSettings settings, CancellationToken cancellationToken = default)
        => _json.SaveAsync(SettingsFile, settings);

    public async Task<IReadOnlyList<PaymentConferenceBulletinFile>> LoadFurrielBulletinsAsync(CancellationToken cancellationToken = default)
    {
        var store = await _json.LoadAsync<FurrielIndexStore>(_paths.FurrielIndexFile) ?? new FurrielIndexStore();
        return store.Files
            .Where(x => !string.IsNullOrWhiteSpace(x.StoredPath) && File.Exists(x.StoredPath))
            .OrderByDescending(x => ParseDate(x.Date))
            .ThenByDescending(x => ToInt(x.Bulletin))
            .Select(x => new PaymentConferenceBulletinFile
            {
                Id = string.IsNullOrWhiteSpace(x.Id) ? HashText(x.StoredPath)[..16] : x.Id,
                Bulletin = x.Bulletin,
                Bar = x.Bar,
                Date = x.Date,
                OriginalName = string.IsNullOrWhiteSpace(x.OriginalName) ? Path.GetFileName(x.StoredPath) : x.OriginalName,
                Path = x.StoredPath,
                Source = "Índice do Boletim Furriel",
                Pages = x.Pages,
                Status = "Pronto"
            })
            .ToList();
    }

    public async Task<PaymentConferenceBulletinFile> BuildFileFromPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        var pages = await _pdfText.ExtractPagesAsync(path, cancellationToken);
        var metadata = ExtractBulletinMetadata(pages, path);
        var items = ParseExpectedItems(path, pages, null, cancellationToken);
        return new PaymentConferenceBulletinFile
        {
            Id = HashText(Path.GetFullPath(path))[..16],
            Bulletin = metadata.Bulletin,
            Bar = metadata.Bar,
            Date = metadata.Date,
            OriginalName = Path.GetFileName(path),
            Path = path,
            Source = "PDF avulso",
            Pages = pages.Count,
            ExpectedItems = items.Count,
            Status = items.Count > 0 ? "Itens encontrados" : "Sem pagamento identificado"
        };
    }

    public async Task<PaymentConferenceResult> RunAsync(
        IEnumerable<string> bulletinPaths,
        PaymentConferenceSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new PaymentConferenceResult();
        var paths = bulletinPaths.Where(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) throw new InvalidOperationException("Selecione pelo menos um aditamento do furriel.");

        var allMilitary = await _repository.GetAllAsync(cancellationToken);
        var militaryByCpf = allMilitary.Where(x => Digits(x.Cpf).Length == 11)
            .GroupBy(x => Digits(x.Cpf), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var militaryByPrec = allMilitary.Where(x => Digits(x.PrecCp).Length >= 6)
            .GroupBy(x => Digits(x.PrecCp).TrimStart('0'), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var expected = new List<PaymentConferenceExpectedItem>();
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[index];
            progress?.Report($"Lendo aditamento {index + 1}/{paths.Count}: {Path.GetFileName(path)}");
            try
            {
                var pages = await _pdfText.ExtractPagesAsync(path, cancellationToken);
                expected.AddRange(ParseExpectedItems(path, pages, settings, cancellationToken));
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
                await _log.WriteAsync("Falha ao ler aditamento para conferência de pagamento.", ex);
            }
        }

        foreach (var item in expected)
        {
            MatchMilitary(item, militaryByCpf, militaryByPrec, allMilitary);
        }
        result.ExpectedItems = expected;

        for (var index = 0; index < expected.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = expected[index];
            progress?.Report($"Conferindo {index + 1}/{expected.Count}: {item.Name}");
            var row = await CheckItemAsync(item, allMilitary, settings, cancellationToken);
            result.Rows.Add(row);
        }

        result.RubricHits = result.Rows
            .Where(x => !string.IsNullOrWhiteSpace(x.PaystubPath) && !string.IsNullOrWhiteSpace(x.RubricsFound))
            .SelectMany(x => ParseRubricHitsFromText(x.Military, x.Cpf, x.PaystubPath, x.RubricsFound))
            .ToList();
        result.Summary = BuildSummary(result.Rows, expected.Count);
        return result;
    }

    public async Task<string> ExportCsvAsync(PaymentConferenceResult result, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ReportsDirectory);
        var path = LastCsvFile;
        var sb = new StringBuilder();
        sb.AppendLine("Status;Militar;CPF;PREC;Boletim;Data;Pagina;Tipo;Modo;Prefixo esperado;Esperado;Recebido;Diferenca;Rubricas;Contracheque;Observacao");
        foreach (var row in result.Rows)
        {
            sb.AppendLine(string.Join(';', new[]
            {
                Csv(row.Status), Csv(row.Military), Csv(MilitaryFormatting.FormatCpf(row.Cpf)), Csv(row.PrecCp), Csv(row.Bulletin), Csv(row.BulletinDate), Csv(row.BulletinPage.ToString(CultureInfo.InvariantCulture)),
                Csv(row.PaymentType), Csv(row.PaymentMode), Csv(row.ExpectedRubricPrefix), Csv(row.ExpectedAmountText), Csv(row.PaidAmountText), Csv(row.DifferenceText),
                Csv(row.RubricsFound), Csv(row.PaystubPath), Csv(row.Notes)
            }));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, cancellationToken);
        return path;
    }

    public void OpenBulletinAtItem(PaymentConferenceExpectedItem item)
        => OpenPdfAndSearch(item.BulletinPath, BestSearchTerm(item), item.DocumentOccurrence);

    public void OpenBulletinAtRow(PaymentConferenceResultRow row)
        => OpenPdfAndSearch(row.BulletinPath, string.IsNullOrWhiteSpace(row.Cpf) ? row.Military : MilitaryFormatting.FormatCpf(row.Cpf), row.DocumentOccurrence);

    public void OpenPaystubAtRow(PaymentConferenceResultRow row)
    {
        var firstCode = Regex.Match(row.RubricsFound ?? string.Empty, @"\b[A-Z]{1,3}\d{3,5}\b").Value;
        var term = !string.IsNullOrWhiteSpace(firstCode)
            ? firstCode
            : KeywordsFor(row.PaymentType).FirstOrDefault() ?? row.PaymentType;
        OpenPdfAndSearch(row.PaystubPath, term, 1);
    }

    private async Task<PaymentConferenceResultRow> CheckItemAsync(
        PaymentConferenceExpectedItem item,
        IReadOnlyList<MilitaryRecord> allMilitary,
        PaymentConferenceSettings settings,
        CancellationToken cancellationToken)
    {
        var row = new PaymentConferenceResultRow
        {
            Bulletin = item.Bulletin,
            BulletinDate = item.BulletinDate,
            BulletinPath = item.BulletinPath,
            BulletinPage = item.Page,
            DocumentOccurrence = item.DocumentOccurrence,
            SectionTitle = item.SectionTitle,
            PaymentType = item.PaymentType,
            PaymentMode = item.PaymentMode,
            ExpectedRubricPrefix = item.ExpectedRubricPrefix,
            Military = string.IsNullOrWhiteSpace(item.MatchedMilitaryName) ? item.IdentityText : item.MatchedMilitaryName,
            Rank = item.Rank,
            Cpf = item.Cpf,
            PrecCp = item.PrecCp,
            MilitaryId = item.MatchedMilitaryId,
            ExpectedAmount = item.ExpectedAmount,
            Context = item.Context
        };

        var military = ResolveMilitary(item, allMilitary);
        if (military is null)
        {
            row.Status = "NÃO ACHOU MILITAR";
            row.Severity = "warning";
            row.Notes = "O militar aparece no aditamento, mas não foi localizado no banco por CPF/PREC/nome. A conferência do contracheque foi tentada pelos dados do aditamento.";
            military = new MilitaryRecord { Rank = item.Rank, Name = item.Name, Cpf = item.Cpf, PrecCp = item.PrecCp };
        }

        var paystubPath = await _paystubs.FindBestInDirectoryAsync(military, settings.Month, settings.Year, settings.PaystubFolder, cancellationToken);
        if (string.IsNullOrWhiteSpace(paystubPath) || !File.Exists(paystubPath))
        {
            row.Status = "SEM CONTRACHEQUE";
            row.Severity = "danger";
            row.Notes = $"Não encontrei contracheque de {settings.Month:00}/{settings.Year} para CPF/PREC/nome informado.";
            return row;
        }

        row.PaystubPath = paystubPath;
        try
        {
            var text = await _pdfText.ExtractAsync(paystubPath, cancellationToken);
            var rubrics = ReadRubrics(text);
            var matches = FindRubricMatches(item, rubrics, settings.RequirePrefix);
            row.RubricsFound = string.Join(" | ", matches.Select(x => x.CompactText).Distinct());
            row.PaidAmount = matches.Sum(x => x.Value);

            if (matches.Count == 0)
            {
                row.Status = "NÃO RECEBEU";
                row.Severity = "danger";
                row.Notes = $"Contracheque localizado, mas não achei rubrica compatível com {item.PaymentType} / {item.PaymentMode}. Regra: {item.ExpectedRubricRule}.";
                return row;
            }

            if (item.ExpectedAmount > 0)
            {
                var diff = Math.Abs(row.PaidAmount - item.ExpectedAmount);
                if (diff <= Math.Max(0.01, settings.Tolerance))
                {
                    row.Status = "OK";
                    row.Severity = "success";
                    row.Notes = "Valor e rubrica compatíveis com a publicação.";
                }
                else
                {
                    row.Status = "VALOR DIVERGENTE";
                    row.Severity = "warning";
                    row.Notes = $"Rubrica localizada, porém o valor recebido diverge do publicado em {MilitaryFormatting.FormatMoney(row.Difference)}.";
                }
            }
            else
            {
                row.Status = "ACHOU RUBRICA";
                row.Severity = "success";
                row.Notes = "A publicação não trouxe valor fechado; o SIGFUR confirmou a existência de rubrica compatível.";
            }
        }
        catch (Exception ex)
        {
            row.Status = "ERRO AO LER";
            row.Severity = "danger";
            row.Notes = ex.Message;
            await _log.WriteAsync($"Falha na conferência do contracheque: {paystubPath}", ex);
        }
        return row;
    }

    private static List<PaymentConferenceExpectedItem> ParseExpectedItems(
        string pdfPath,
        IReadOnlyList<string> pages,
        PaymentConferenceSettings? settings,
        CancellationToken cancellationToken)
    {
        var metadata = ExtractBulletinMetadata(pages, pdfPath);
        var pageLines = pages.Select((text, page) => new PageLine(page + 1, text ?? string.Empty)).ToList();
        var allText = string.Join("\n", pages);
        var allLines = allText.Split('\n').ToList();
        var startLine = allLines.FindIndex(x => Normalize(x).Contains("PAGAMENTO PESSOAL", StringComparison.Ordinal));
        if (startLine < 0) startLine = 0;
        var endLine = allLines.FindIndex(startLine, x => Normalize(x).Contains("4ª PARTE", StringComparison.Ordinal) || Normalize(x).Contains("JUSTICA E DISCIPLINA", StringComparison.Ordinal));
        if (endLine < 0) endLine = allLines.Count;
        var usableText = string.Join("\n", allLines.Skip(startLine).Take(Math.Max(0, endLine - startLine)));

        var sections = SplitPaymentSections(usableText).ToList();
        var occurrencesBefore = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PaymentConferenceExpectedItem>();

        foreach (var section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classification = ClassifySection(section.Title, section.Text);
            if (!ShouldInclude(classification.Type, settings)) continue;

            var people = ExtractPeopleFromSection(section.Text).ToList();
            foreach (var person in people)
            {
                var search = string.IsNullOrWhiteSpace(person.Cpf) ? person.Name : MilitaryFormatting.FormatCpf(person.Cpf);
                var occurrence = 1;
                if (!string.IsNullOrWhiteSpace(search))
                {
                    occurrence = occurrencesBefore.TryGetValue(search, out var current) ? current + 1 : 1;
                    occurrencesBefore[search] = occurrence;
                }
                var item = new PaymentConferenceExpectedItem
                {
                    Bulletin = metadata.Bulletin,
                    BulletinDate = metadata.Date,
                    BulletinPath = pdfPath,
                    Page = FindPageForContext(pageLines, person.Cpf, person.Name),
                    DocumentOccurrence = occurrence,
                    SectionTitle = section.Title,
                    PaymentType = classification.Type,
                    PaymentMode = classification.Mode,
                    ExpectedRubricPrefix = classification.Prefix,
                    ExpectedRubricRule = classification.Rule,
                    Rank = person.Rank,
                    Name = person.Name,
                    Cpf = person.Cpf,
                    PrecCp = person.Prec,
                    ExpectedAmount = person.Amount,
                    Context = CleanContext(person.Context)
                };
                result.Add(item);
            }
        }
        return result;
    }

    private static IEnumerable<PaymentSection> SplitPaymentSections(string text)
    {
        var lines = (text ?? string.Empty).Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var startIndexes = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var norm = Normalize(lines[i]);
            if (Regex.IsMatch(norm, @"^[A-Z]\.?\s+[A-Z].{3,}") && LooksLikePaymentHeading(norm)) startIndexes.Add(i);
        }
        if (startIndexes.Count == 0)
        {
            yield return new PaymentSection("PAGAMENTO PESSOAL", string.Join("\n", lines));
            yield break;
        }

        for (var s = 0; s < startIndexes.Count; s++)
        {
            var start = startIndexes[s];
            var end = s + 1 < startIndexes.Count ? startIndexes[s + 1] : lines.Count;
            var title = Regex.Replace(lines[start], @"^[a-zA-Z]\.?\s+", string.Empty).Trim();
            var sectionText = string.Join("\n", lines.Skip(start).Take(end - start));
            yield return new PaymentSection(title, sectionText);
        }
    }

    private static bool LooksLikePaymentHeading(string normalized)
        => normalized.Contains("AUXILIO", StringComparison.Ordinal)
           || normalized.Contains("TRANSPORTE", StringComparison.Ordinal)
           || normalized.Contains("FERIAS", StringComparison.Ordinal)
           || normalized.Contains("GRATIFICACAO", StringComparison.Ordinal)
           || normalized.Contains("REPRESENTACAO", StringComparison.Ordinal)
           || normalized.Contains("HABILITACAO", StringComparison.Ordinal)
           || normalized.Contains("ALIMENTACAO", StringComparison.Ordinal)
           || normalized.Contains("PAGAMENTO", StringComparison.Ordinal)
           || normalized.Contains("SAQUE", StringComparison.Ordinal)
           || normalized.Contains("ORDEM", StringComparison.Ordinal);

    private static IEnumerable<ExtractedPerson> ExtractPeopleFromSection(string sectionText)
    {
        var lines = (sectionText ?? string.Empty).Split('\n').Select(x => Regex.Replace(x, @"\s+", " ").Trim()).Where(x => x.Length > 0).ToList();
        var nameIndexes = new List<(int Index, Match Match)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = RankNameRegex().Match(lines[i]);
            if (m.Success) nameIndexes.Add((i, m));
        }

        var previousPersonEnd = 0;
        for (var n = 0; n < nameIndexes.Count; n++)
        {
            var (idx, match) = nameIndexes[n];
            var nextName = n + 1 < nameIndexes.Count ? nameIndexes[n + 1].Index : lines.Count;
            var localEnd = Math.Min(lines.Count, nextName);
            var localLines = lines.Skip(idx).Take(Math.Min(8, localEnd - idx)).ToList();
            var local = string.Join("\n", localLines);
            var cpf = CpfRegex().Match(local).Value;
            var precMatch = PrecRegex().Match(local);
            var prec = precMatch.Success ? precMatch.Groups[1].Value : string.Empty;
            if (string.IsNullOrWhiteSpace(cpf) && string.IsNullOrWhiteSpace(prec)) continue;

            var cpfRelative = localLines.FindIndex(x => CpfRegex().IsMatch(x) || PrecRegex().IsMatch(x));
            var cpfLine = cpfRelative >= 0 ? idx + cpfRelative : idx;
            var contextStart = Math.Max(0, Math.Min(previousPersonEnd, idx));
            var contextLines = lines.Skip(contextStart).Take(Math.Max(1, cpfLine - contextStart + 1)).ToList();

            // Em auxílio-transporte o valor total costuma vir depois da linha CPF/PREC.
            // Já nos atrasados de habilitação/férias/gratificação o valor vem antes do nome;
            // por isso só anexamos linhas pós-CPF quando elas forem explicitamente linhas de valor.
            for (var j = cpfLine + 1; j < nextName && j < cpfLine + 6 && j < lines.Count; j++)
            {
                var norm = Normalize(lines[j]);
                if (norm.StartsWith("VALOR", StringComparison.Ordinal) || (norm.Contains("VALOR", StringComparison.Ordinal) && MoneyRegex().IsMatch(lines[j])))
                    contextLines.Add(lines[j]);
                else if (norm.StartsWith("SEJA", StringComparison.Ordinal) || norm.StartsWith("EM CONSEQUENCIA", StringComparison.Ordinal))
                    break;
            }

            previousPersonEnd = Math.Max(previousPersonEnd, cpfLine + 1);
            var context = string.Join("\n", contextLines);
            var amount = ExtractExpectedAmount(context);
            yield return new ExtractedPerson(
                Rank: CleanRank(match.Groups["rank"].Value),
                Name: CleanName(match.Groups["name"].Value),
                Cpf: MilitaryFormatting.FormatCpf(cpf),
                Prec: Digits(prec),
                Amount: amount,
                Context: context);
        }
    }

    private static double ExtractExpectedAmount(string context)
    {
        var valueTotal = Regex.Match(context ?? string.Empty, @"Valor\s+Total\s*:\s*(?:R\$\s*)?(?<v>\d{1,3}(?:\.\d{3})*,\d{2}|\d+,\d{2})", RegexOptions.IgnoreCase);
        if (valueTotal.Success) return ParseMoney(valueTotal.Groups["v"].Value);
        var values = MoneyRegex().Matches(context ?? string.Empty).Select(x => ParseMoney(x.Value)).Where(x => x > 0).ToList();
        if (values.Count == 0) return 0;
        if (Normalize(context).Contains("CORRESPONDENTE", StringComparison.Ordinal) || Normalize(context).Contains("SAQUE", StringComparison.Ordinal))
            return values.Sum();
        return values.Count == 1 ? values[0] : values[^1];
    }

    private static PaymentClassification ClassifySection(string title, string text)
    {
        var blob = Normalize(title + " " + text);
        var type = "Outros Pagamentos";
        var rule = "Rubrica compatível por descrição";
        if (blob.Contains("AUXILIO TRANSPORTE", StringComparison.Ordinal) || blob.Contains("AUX TRANSPORTE", StringComparison.Ordinal)) { type = "Auxílio-Transporte"; rule = "descrição contém TRANSPORTE; prefixo N normal ou A atrasado"; }
        else if (blob.Contains("GRATIFICACAO DE REPRESENTACAO", StringComparison.Ordinal) || blob.Contains("GRAT REP", StringComparison.Ordinal)) { type = "Gratificação de Representação"; rule = "descrição contém GRATIFICAÇÃO/REPRESENTAÇÃO; prefixo N normal ou A atrasado"; }
        else if (blob.Contains("FERIAS", StringComparison.Ordinal)) { type = "Férias"; rule = "descrição contém FÉRIAS; prefixo N normal ou A atrasado"; }
        else if (blob.Contains("ADICIONAL HABILITACAO", StringComparison.Ordinal) || blob.Contains("HABILITACAO", StringComparison.Ordinal)) { type = "Adicional Habilitação"; rule = "descrição contém HABILITAÇÃO; prefixo N normal ou A atrasado"; }
        else if (blob.Contains("AUXILIO ALIMENTACAO", StringComparison.Ordinal) || blob.Contains("ALIMENTACAO", StringComparison.Ordinal)) { type = "Auxílio-Alimentação"; rule = "descrição contém ALIMENTAÇÃO; prefixo N normal ou A atrasado"; }

        var mode = "Conferir";
        var prefix = string.Empty;
        if (blob.Contains("ATRASAD", StringComparison.Ordinal) || blob.Contains("EXERCICIO ANTERIOR", StringComparison.Ordinal)) { mode = "Saque de atrasado"; prefix = "A"; }
        else if (blob.Contains("DESPESA A ANULAR", StringComparison.Ordinal) || blob.Contains("ANUL", StringComparison.Ordinal) || blob.Contains("DESCONTO", StringComparison.Ordinal)) { mode = "Despesa a anular/desconto"; prefix = "D"; }
        else if (blob.Contains("ORDEM", StringComparison.Ordinal) || blob.Contains("IMPLANT", StringComparison.Ordinal) || blob.Contains("CONCESS", StringComparison.Ordinal)) { mode = "Normal/implantação"; prefix = "N"; }
        return new PaymentClassification(type, mode, prefix, string.IsNullOrWhiteSpace(prefix) ? rule : $"{rule}; rubrica deve iniciar por {prefix}");
    }

    private static bool ShouldInclude(string type, PaymentConferenceSettings? settings)
    {
        if (settings is null) return true;
        return type switch
        {
            "Férias" => settings.IncludeVacation,
            "Auxílio-Transporte" => settings.IncludeTransportAid,
            "Gratificação de Representação" => settings.IncludeGratification,
            "Adicional Habilitação" => settings.IncludeQualification,
            _ => settings.IncludeOthers
        };
    }

    private static List<PaystubRubric> ReadRubrics(string text)
    {
        var lines = (text ?? string.Empty).Split('\n').Select(OneLine).Where(x => x.Length > 0).ToList();
        var result = new List<PaystubRubric>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string code, string block)
        {
            block = OneLine(block);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(block)) return;
            var moneyValues = MoneyRegex().Matches(block).Select(x => ParseMoney(x.Value)).Where(x => x > 0).ToList();
            var money = moneyValues.Count == 0 ? 0 : moneyValues[0];
            if (money <= 0) return;
            var normalized = Normalize(block);
            var key = code + "|" + normalized[..Math.Min(100, normalized.Length)] + "|" + money.ToString("0.00", CultureInfo.InvariantCulture);
            if (!seen.Add(key)) return;
            result.Add(new PaystubRubric(code.ToUpperInvariant(), DescriptionFromBlock(code, block), block, normalized, money));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var match = RubricCodeRegex().Match(Normalize(lines[i]));
            if (!match.Success) continue;
            var parts = new List<string> { lines[i] };
            for (var j = i + 1; j < lines.Count && j <= i + 5; j++)
            {
                var next = Normalize(lines[j]);
                if (RubricCodeRegex().IsMatch(next) || IsFooter(next)) break;
                parts.Add(lines[j]);
            }
            Add(match.Groups[1].Value, string.Join(" ", parts));
        }

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

    private static List<PaystubRubric> FindRubricMatches(PaymentConferenceExpectedItem item, List<PaystubRubric> rubrics, bool requirePrefix)
    {
        var keywords = KeywordsFor(item.PaymentType);
        var prefix = (item.ExpectedRubricPrefix ?? string.Empty).Trim().ToUpperInvariant();
        var primary = rubrics.Where(r =>
            (keywords.Count == 0 || keywords.Any(k => r.Normalized.Contains(k, StringComparison.Ordinal))) &&
            (!requirePrefix || string.IsNullOrWhiteSpace(prefix) || r.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Fallback profissional: se a extração do CPEx vier com descrição quebrada, usa sufixos/códigos conhecidos
        // sem abandonar a regra principal. O alerta aparece como "achou rubrica" ou divergência conforme valor.
        if (primary.Count == 0 && item.PaymentType == "Auxílio-Transporte")
            primary = rubrics.Where(r => r.Code.EndsWith("0095", StringComparison.OrdinalIgnoreCase) && (!requirePrefix || string.IsNullOrWhiteSpace(prefix) || r.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
        if (primary.Count == 0 && !requirePrefix && !string.IsNullOrWhiteSpace(prefix))
            primary = rubrics.Where(r => keywords.Any(k => r.Normalized.Contains(k, StringComparison.Ordinal))).ToList();
        return primary;
    }

    private static IReadOnlyList<string> KeywordsFor(string type)
    {
        return type switch
        {
            "Auxílio-Transporte" => ["AUXILIO TRANSPORTE", "AUX TRANSP", "TRANSPORTE"],
            "Férias" => ["FERIAS"],
            "Gratificação de Representação" => ["GRATIFICACAO", "REPRESENTACAO", "GRAT REP"],
            "Adicional Habilitação" => ["HABILITACAO", "ADICIONAL DE HABILITACAO", "ADIC HABILIT"],
            "Auxílio-Alimentação" => ["AUXILIO ALIMENTACAO", "AUX ALIMENT", "ALIMENTACAO"],
            _ => []
        };
    }

    private void MatchMilitary(
        PaymentConferenceExpectedItem item,
        Dictionary<string, MilitaryRecord> byCpf,
        Dictionary<string, MilitaryRecord> byPrec,
        IReadOnlyList<MilitaryRecord> allMilitary)
    {
        MilitaryRecord? match = null;
        var cpf = Digits(item.Cpf);
        var prec = Digits(item.PrecCp).TrimStart('0');
        if (cpf.Length == 11 && byCpf.TryGetValue(cpf, out var byCpfRecord)) match = byCpfRecord;
        else if (prec.Length >= 6 && byPrec.TryGetValue(prec, out var byPrecRecord)) match = byPrecRecord;
        else
        {
            var normalizedName = Normalize(item.Name);
            match = allMilitary.Select(x => (Item: x, Score: NameScore(normalizedName, Normalize(x.Name)) + NameScore(normalizedName, Normalize(x.WarName))))
                .Where(x => x.Score >= 55)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Item)
                .FirstOrDefault();
        }
        if (match is null)
        {
            item.MatchStatus = "Não localizado no banco";
            return;
        }
        item.MatchedMilitaryId = match.Id;
        item.MatchedMilitaryName = $"{MilitaryRankService.ShortName(match.Rank)} {match.Name}".Trim();
        item.MatchStatus = "Localizado no banco";
    }

    private static MilitaryRecord? ResolveMilitary(PaymentConferenceExpectedItem item, IReadOnlyList<MilitaryRecord> allMilitary)
    {
        if (item.MatchedMilitaryId is int id)
        {
            var byId = allMilitary.FirstOrDefault(x => x.Id == id);
            if (byId is not null) return byId;
        }
        var cpf = Digits(item.Cpf);
        if (cpf.Length == 11)
        {
            var byCpf = allMilitary.FirstOrDefault(x => Digits(x.Cpf) == cpf);
            if (byCpf is not null) return byCpf;
        }
        var prec = Digits(item.PrecCp).TrimStart('0');
        if (prec.Length >= 6)
        {
            var byPrec = allMilitary.FirstOrDefault(x => Digits(x.PrecCp).TrimStart('0') == prec);
            if (byPrec is not null) return byPrec;
        }
        return null;
    }

    private static PaymentConferenceSummary BuildSummary(IEnumerable<PaymentConferenceResultRow> rows, int expectedCount)
    {
        var list = rows.ToList();
        return new PaymentConferenceSummary
        {
            Expected = expectedCount,
            Ok = list.Count(x => x.Status is "OK" or "ACHOU RUBRICA"),
            MissingPaystub = list.Count(x => x.Status == "SEM CONTRACHEQUE"),
            MissingRubric = list.Count(x => x.Status == "NÃO RECEBEU"),
            Divergent = list.Count(x => x.Status == "VALOR DIVERGENTE"),
            Attention = list.Count(x => x.Status is "NÃO ACHOU MILITAR" or "ERRO AO LER")
        };
    }

    private static (string Bulletin, string Bar, string Date) ExtractBulletinMetadata(IReadOnlyList<string> pages, string path)
    {
        var head = string.Join("\n", pages.Take(2));
        var bulletin = Regex.Match(head, @"ADITAMENTO\s+DO\s+FURRIEL\s+N[º°O]?\s*(\d{1,4}/\d{4}|\d{1,4})", RegexOptions.IgnoreCase).Groups[1].Value;
        var bar = Regex.Match(head, @"\bBAR\s*(\d{1,4})\b", RegexOptions.IgnoreCase).Groups[1].Value;
        var date = Regex.Match(head, @"(\d{1,2}\s+de\s+[A-Za-zÀ-ÿ]+\s+de\s+20\d{2})", RegexOptions.IgnoreCase).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(date)) date = Regex.Match(head, @"\b\d{1,2}/\d{1,2}/20\d{2}\b").Value;
        if (string.IsNullOrWhiteSpace(bulletin)) bulletin = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(?<!\d)(\d{1,4})(?!\d)").Groups[1].Value;
        return (string.IsNullOrWhiteSpace(bulletin) ? "—" : bulletin, bar, string.IsNullOrWhiteSpace(date) ? "—" : ToBrDate(date));
    }

    private static int FindPageForContext(IReadOnlyList<PageLine> pages, string cpf, string name)
    {
        var cpfDigits = Digits(cpf);
        foreach (var page in pages)
        {
            if (cpfDigits.Length == 11 && Digits(page.Text).Contains(cpfDigits, StringComparison.Ordinal)) return page.Page;
        }
        var n = Normalize(name);
        foreach (var page in pages)
            if (!string.IsNullOrWhiteSpace(n) && Normalize(page.Text).Contains(n, StringComparison.Ordinal)) return page.Page;
        return 1;
    }

    private static List<PaymentConferenceRubricHit> ParseRubricHitsFromText(string military, string cpf, string paystubPath, string compact)
    {
        var result = new List<PaymentConferenceRubricHit>();
        foreach (var part in compact.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(part, @"^(?<code>[A-Z]{1,3}\d{3,5})\s+-\s+(?<desc>.*?)\s+=\s+(?<val>R\$.*)$", RegexOptions.IgnoreCase);
            result.Add(new PaymentConferenceRubricHit
            {
                Military = military,
                Cpf = cpf,
                PaystubPath = paystubPath,
                Code = m.Success ? m.Groups["code"].Value : string.Empty,
                Description = m.Success ? m.Groups["desc"].Value : part,
                Value = m.Success ? ParseMoney(m.Groups["val"].Value) : 0,
                Line = part
            });
        }
        return result;
    }

    private static string DescriptionFromBlock(string code, string block)
    {
        var text = Regex.Replace(block ?? string.Empty, "^" + Regex.Escape(code) + @"\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        text = MoneyRegex().Replace(text, string.Empty).Trim();
        text = Regex.Replace(text, @"\s+\d{1,3},\d{2}\s+[RD]\s+[-+]?.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return text.Length == 0 ? block ?? string.Empty : text;
    }

    private static string BestSearchTerm(PaymentConferenceExpectedItem item)
        => !string.IsNullOrWhiteSpace(item.Cpf) ? MilitaryFormatting.FormatCpf(item.Cpf) : item.Name;

    private static void OpenPdfAndSearch(string path, string searchTerm, int occurrence)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        ShellService.OpenPath(path);
        var term = (searchTerm ?? string.Empty).Trim();
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(term)) return;
        try { Clipboard.SetText(term); } catch { }
        _ = Task.Run(async () =>
        {
            await Task.Delay(1650);
            SendCtrlFAndPaste();
            await Task.Delay(550);
            PressKey(VkEscape);
            await Task.Delay(120);
            for (var index = 1; index < Math.Max(1, occurrence); index++)
            {
                PressKey(VkF3);
                await Task.Delay(110);
            }
        });
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static string CleanContext(string value)
        => Regex.Replace(value ?? string.Empty, @"[ \t]+", " ").Trim();

    private static string CleanRank(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static string CleanName(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '-', '.', ':');
    private static string OneLine(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static bool IsFooter(string value) => new[] { "DATA IMP", "DEP IR", "ISENTO IR", "RECEITA", "DESPESA", "LIQUIDO", "BANCO", "AGENCIA", "C/C", "SITUACAO" }.Any(value.StartsWith);
    private static int IndexOfNormalized(string source, string search) => Normalize(source).IndexOf(Normalize(search), StringComparison.Ordinal);
    private static string Digits(string? value) => Regex.Replace(value ?? string.Empty, @"\D+", string.Empty);
    private static int ToInt(string? value) => int.TryParse(Regex.Match(value ?? string.Empty, @"\d+").Value, out var number) ? number : 0;
    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static DateTime ParseDate(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        foreach (var fmt in new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "yyyy-MM-dd" })
            if (DateTime.TryParseExact(text, fmt, PtBr, DateTimeStyles.None, out var dt)) return dt;
        if (DateTime.TryParse(text, PtBr, DateTimeStyles.None, out var parsed)) return parsed;
        return DateTime.MinValue;
    }

    private static string ToBrDate(string? value)
    {
        var parsed = ParseDate(value);
        return parsed == DateTime.MinValue ? (value ?? "—") : parsed.ToString("dd/MM/yyyy", PtBr);
    }

    private static double ParseMoney(string? value)
    {
        var text = (value ?? string.Empty).Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (text.Length == 0) return 0;
        if (text.Contains(',')) text = text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? Math.Abs(number) : 0;
    }

    private static int NameScore(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 0;
        if (a == b) return 100;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 75;
        var aw = Regex.Matches(a, @"[A-Z0-9]{3,}").Select(x => x.Value).Distinct().ToList();
        var bw = Regex.Matches(b, @"[A-Z0-9]{3,}").Select(x => x.Value).Distinct().ToHashSet(StringComparer.Ordinal);
        if (aw.Count == 0 || bw.Count == 0) return 0;
        var hits = aw.Count(bw.Contains);
        return (int)Math.Round((double)hits / aw.Count * 100);
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToUpperInvariant(c));
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"\b(?<rank>(?:S\s*Ten|Sub\s*Ten|Asp|Cap|Maj|Ten\s*Cel|Cel|1[º°]\s*Ten|2[º°]\s*Ten|1[º°]\s*Sgt|2[º°]\s*Sgt|3[º°]\s*Sgt|Cb|Sd)(?:\s+EF\s+(?:PROFL|VRV))?)\s+(?<name>[A-ZÀ-Ý][A-ZÀ-Ý\s.'\-]{3,})$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RankNameRegex();

    [GeneratedRegex(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b", RegexOptions.Compiled)]
    private static partial Regex CpfRegex();

    [GeneratedRegex(@"Prec\s*[-/]?\s*CP\s*[:\-]?\s*([\d.\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PrecRegex();

    [GeneratedRegex(@"(?<!\d)(?:R\$\s*)?[-+]?\d{1,3}(?:\.\d{3})*,\d{2}|(?<!\d)(?:R\$\s*)?[-+]?\d+,\d{2}(?!\d)", RegexOptions.Compiled)]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"\b([A-Z]{1,3}\d{3,5})\b", RegexOptions.Compiled)]
    private static partial Regex RubricCodeRegex();

    private sealed record PaymentSection(string Title, string Text);
    private sealed record PaymentClassification(string Type, string Mode, string Prefix, string Rule);
    private sealed record ExtractedPerson(string Rank, string Name, string Cpf, string Prec, double Amount, string Context);
    private sealed record PageLine(int Page, string Text);
    private sealed record PaystubRubric(string Code, string Description, string Line, string Normalized, double Value)
    {
        public string CompactText => $"{Code} - {Description} = {MilitaryFormatting.FormatMoney(Value)}";
    }

    // Win32: abrir o PDF e deixar a pesquisa pronta no termo do militar/rubrica.
    private const ushort VkControl = 0x11;
    private const ushort VkF = 0x46;
    private const ushort VkV = 0x56;
    private const ushort VkEscape = 0x1B;
    private const ushort VkF3 = 0x72;
    private const uint KeyeventfKeyup = 0x0002;

    private static void SendCtrlFAndPaste()
    {
        Key(VkControl, false); Key(VkF, false); Key(VkF, true); Key(VkControl, true);
        Thread.Sleep(120);
        Key(VkControl, false); Key(VkV, false); Key(VkV, true); Key(VkControl, true);
    }

    private static void PressKey(ushort key) { Key(key, false); Thread.Sleep(35); Key(key, true); }
    private static void Key(ushort key, bool up) => keybd_event((byte)key, 0, up ? KeyeventfKeyup : 0, UIntPtr.Zero);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
