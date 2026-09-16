using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class ExercisePreviousImportService
{
    private readonly ExercisePreviousRepository _repository;
    private readonly LogService _log;

    public ExercisePreviousImportService(ExercisePreviousRepository repository, LogService log)
    {
        _repository = repository;
        _log = log;
    }

    public async Task<ExercisePreviousImportResult> ImportAsync(
        string path,
        IReadOnlyList<ExercisePreviousCode> defaultCodes,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var result = new ExercisePreviousImportResult { FilePath = path };
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Planilha nao encontrada.", path);

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".xls")
                throw new NotSupportedException("Arquivos .xls antigos nao sao lidos com seguranca pelo SIGFUR. Abra no Excel/LibreOffice, salve como .xlsx e importe novamente.");

            progress?.Report("Lendo planilha...");
            List<List<string>> rows;
            try
            {
                rows = await SpreadsheetService.ReadTabularFileAsync(path, cancellationToken);
            }
            catch (IOException ex)
            {
                throw new IOException("Nao consegui abrir a planilha. Verifique se o arquivo esta aberto no Excel, bloqueado pelo OneDrive ou sem permissao de leitura.", ex);
            }

            if (rows.Count == 0) throw new InvalidDataException("A planilha nao possui linhas.");

            progress?.Report("Validando colunas...");
            var header = FindHeader(rows);
            result.HeaderRowNumber = header.RowIndex + 1;
            foreach (var mapped in header.MappedHeaders) result.MappedHeaders.Add(mapped);
            await _log.WriteAsync($"EA Import: arquivo={path}; cabecalho_linha={result.HeaderRowNumber}; colunas={string.Join(", ", result.MappedHeaders)}");

            progress?.Report("Vinculando militares...");
            for (var i = header.RowIndex + 1; i < rows.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = rows[i];
                if (IsBlankRow(row))
                {
                    result.Ignored++;
                    continue;
                }

                result.TotalRowsRead++;
                try
                {
                    var process = BuildProcess(row, header.ColumnMap, defaultCodes);
                    if (!HasMinimumData(process))
                    {
                        result.Ignored++;
                        result.Issues.Add(Issue(i + 1, "Aviso", "Linha sem dados suficientes para criar processo EA.", row));
                        continue;
                    }

                    var resolution = await ResolveMilitaryAsync(process, cancellationToken);
                    if (resolution.Match is not null)
                    {
                        ApplyMilitarySnapshot(process, resolution.Match);
                        if (!process.MilitaryId.HasValue)
                            result.Issues.Add(Issue(i + 1, "Pendente", $"Militar localizado apenas em {resolution.Match.Source}; processo salvo sem vinculo ativo.", row));
                    }
                    else if (!string.IsNullOrWhiteSpace(resolution.Message))
                    {
                        result.Issues.Add(Issue(i + 1, "Pendente", resolution.Message, row));
                    }

                    result.Processes.Add(process);
                    if (result.Processes.Count % 25 == 0)
                        progress?.Report($"Validando linhas... {result.Processes.Count} processo(s) preparado(s)");
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    result.Issues.Add(Issue(i + 1, "Erro", ex.Message, row));
                    await _log.WriteAsync($"EA Import: erro na linha {i + 1}.", ex);
                }
            }

            if (result.Processes.Count == 0)
                throw new InvalidDataException("Nenhuma linha valida foi encontrada para importar.");

            progress?.Report("Salvando...");
            result.Imported = await _repository.SaveImportedProcessesAsync(result.Processes, cancellationToken);
            result.Linked = result.Processes.Count(x => x.MilitaryId.HasValue);
            result.Pending = Math.Max(0, result.Imported - result.Linked);

            progress?.Report("Importacao concluida.");
            await _log.WriteAsync(
                $"EA Import concluida: arquivo={path}; lidas={result.TotalRowsRead}; importadas={result.Imported}; vinculadas={result.Linked}; pendentes={result.Pending}; ignoradas={result.Ignored}; erros={result.Errors}; tempo_ms={started.ElapsedMilliseconds}");
            return result;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"EA Import falhou: arquivo={path}; tempo_ms={started.ElapsedMilliseconds}", ex);
            throw;
        }
    }

    private async Task<(ExercisePreviousMilitarySearchResult? Match, string Message)> ResolveMilitaryAsync(ExercisePreviousProcess process, CancellationToken ct)
    {
        var cpf = ExercisePreviousRepository.Digits(process.Cpf);
        if (cpf.Length == 11)
        {
            var exact = (await _repository.SearchMilitaryAsync(cpf, 20, ct))
                .Where(x => ExercisePreviousRepository.Digits(x.Cpf) == cpf)
                .ToList();
            return PickExact(exact, "CPF");
        }

        var prec = ExercisePreviousRepository.LettersAndDigits(process.PrecCp);
        if (!string.IsNullOrWhiteSpace(prec))
        {
            var exact = (await _repository.SearchMilitaryAsync(process.PrecCp, 20, ct))
                .Where(x => ExercisePreviousRepository.LettersAndDigits(x.PrecCp) == prec)
                .ToList();
            return PickExact(exact, "Prec-CP");
        }

        var identity = ExercisePreviousRepository.LettersAndDigits(process.Identity);
        if (!string.IsNullOrWhiteSpace(identity))
        {
            var exact = (await _repository.SearchMilitaryAsync(process.Identity, 20, ct))
                .Where(x => ExercisePreviousRepository.LettersAndDigits(x.Identity) == identity)
                .ToList();
            return PickExact(exact, "identidade");
        }

        var name = ExercisePreviousRepository.NormalizeSearchText(process.FullName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var candidates = await _repository.SearchMilitaryAsync(process.FullName, 20, ct);
            var exact = candidates.Where(x => ExercisePreviousRepository.NormalizeSearchText(x.FullName) == name).ToList();
            if (exact.Count > 0) return PickExact(exact, "nome completo");

            var strong = candidates.Where(x => !x.WeakMatch && x.Confidence >= 65).ToList();
            if (strong.Count == 1) return (strong[0], string.Empty);
            if (strong.Count > 1) return (null, "Mais de um militar possivel por nome completo. Confira manualmente antes de vincular.");
        }

        if (!string.IsNullOrWhiteSpace(process.WarName))
        {
            var suggestions = await _repository.SearchMilitaryAsync(process.WarName, 8, ct);
            if (suggestions.Count > 0)
                return (null, "Nome de guerra encontrado apenas como sugestao; vinculo nao foi gravado automaticamente.");
        }

        return (null, "Militar nao localizado no banco por CPF, Prec-CP, identidade ou nome completo forte.");

        static (ExercisePreviousMilitarySearchResult? Match, string Message) PickExact(List<ExercisePreviousMilitarySearchResult> exact, string field)
            => exact.Count switch
            {
                1 => (exact[0], string.Empty),
                > 1 => (null, $"Mais de um militar encontrado com o mesmo {field}. Confira manualmente antes de vincular."),
                _ => (null, $"Nenhum militar encontrado com {field} informado.")
            };
    }

    private static HeaderMatch FindHeader(IReadOnlyList<List<string>> rows)
    {
        HeaderMatch? best = null;
        var max = Math.Min(rows.Count, 15);
        for (var i = 0; i < max; i++)
        {
            var match = MapHeader(rows[i], i);
            if (match.ColumnMap.Count == 0) continue;
            if (best is null || match.Score > best.Score) best = match;
        }

        if (best is null || best.Score < 2)
            throw new InvalidDataException("Nao consegui localizar o cabecalho da planilha nas primeiras linhas. Use colunas como Nome, CPF, Prec-CP, Identidade, Posto/Grad ou Periodo.");
        return best;
    }

    private static HeaderMatch MapHeader(IReadOnlyList<string> row, int rowIndex)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mapped = new List<string>();
        for (var i = 0; i < row.Count; i++)
        {
            var raw = Clean(row[i]);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var normalized = NormalizeHeader(raw);
            if (!HeaderAliases.TryGetValue(normalized, out var canonical) || map.ContainsKey(canonical)) continue;
            map[canonical] = i;
            mapped.Add($"{raw} -> {canonical}");
        }
        var identityScore = new[] { "full_name", "cpf", "prec_cp", "identity", "rank" }.Count(map.ContainsKey);
        var processScore = new[] { "period_start", "period_end", "debt_type", "process_number", "requested_value" }.Count(map.ContainsKey);
        return new HeaderMatch(rowIndex, map, mapped, map.Count + identityScore + processScore);
    }

    private static ExercisePreviousProcess BuildProcess(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> map, IReadOnlyList<ExercisePreviousCode> defaultCodes)
    {
        var p = new ExercisePreviousProcess
        {
            PeriodStart = string.Empty,
            PeriodEnd = string.Empty,
            UpdatedThrough = string.Empty,
            RequestDate = string.Empty,
            BulletinDate = string.Empty,
            ProcessYear = DateTime.Today.Year
        };

        foreach (var code in CloneCodes(defaultCodes)) p.Codes.Add(code);

        foreach (var pair in map)
        {
            var value = Get(row, pair.Value);
            if (string.IsNullOrWhiteSpace(value)) continue;
            switch (pair.Key)
            {
                case "rank": p.Rank = value; break;
                case "full_name": p.FullName = value; break;
                case "war_name": p.WarName = value; break;
                case "cpf": p.Cpf = NormalizeCpf(value); break;
                case "prec_cp": p.PrecCp = NormalizeIdentifier(value); break;
                case "identity": p.Identity = NormalizeIdentifier(value); break;
                case "situation": p.Situation = value.ToUpperInvariant(); break;
                case "bank": p.Bank = value; break;
                case "agency": p.Agency = value; break;
                case "account": p.Account = value; break;
                case "birth_date": p.BirthDate = NormalizeDate(value); break;
                case "general_protocol": p.GeneralProtocol = value; break;
                case "section": p.Section = value; break;
                case "subject_number": p.SubjectNumber = value; break;
                case "subject_text": p.SubjectText = value; break;
                case "recipient": p.Recipient = value; break;
                case "object": p.Object = value; break;
                case "phone": p.Phone = value; break;
                case "payment_reason": p.PaymentReason = value; break;
                case "requested_value": p.RequestedValue = value; break;
                case "process_number": p.ProcessNumber = value; break;
                case "process_year": p.ProcessYear = TryInt(value) ?? p.ProcessYear; break;
                case "request_date": p.RequestDate = NormalizeDate(value); break;
                case "request_date_words": p.RequestDateInWords = value; break;
                case "bulletin_number": p.BulletinNumber = value; break;
                case "bulletin_date": p.BulletinDate = NormalizeDate(value); break;
                case "debt_type": p.DebtType = value; break;
                case "period_start": p.PeriodStart = NormalizeDate(value); break;
                case "period_end": p.PeriodEnd = NormalizeDate(value); break;
                case "updated_through": p.UpdatedThrough = NormalizeMonth(value); break;
                case "previous_exercise_type": p.PreviousExerciseType = value; break;
                case "ea_indicative": p.EaIndicative = value; break;
                case "has_judicial_pension": p.HasJudicialPension = NormalizeYesNo(value); break;
                case "right_doc": p.RightMaterializationDocument = value; break;
                case "bulletin_recorded": p.BulletinThatRecorded = value; break;
                case "non_payment": p.NonPaymentExplanation = value; break;
            }
        }

        var entry = BuildEntry(row, map, defaultCodes);
        if (entry is not null) p.Entries.Add(entry);
        return p;
    }

    private static ExercisePreviousEntry? BuildEntry(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> map, IReadOnlyList<ExercisePreviousCode> defaultCodes)
    {
        var hasMoney = map.ContainsKey("entry_received") || map.ContainsKey("entry_due");
        if (!hasMoney) return null;

        int? year = null;
        int? month = null;
        if (map.TryGetValue("entry_competence", out var competenceColumn))
            (year, month) = ParseCompetence(Get(row, competenceColumn));
        if (map.TryGetValue("entry_year", out var yearColumn)) year = TryInt(Get(row, yearColumn)) ?? year;
        if (map.TryGetValue("entry_month", out var monthColumn)) month = ParseMonth(Get(row, monthColumn)) ?? month;
        if (year is null || month is null or < 1 or > 12) return null;

        var codeOrder = 1;
        if (map.TryGetValue("entry_code", out var codeColumn))
            codeOrder = ResolveCodeOrder(Get(row, codeColumn), defaultCodes) ?? 1;

        return new ExercisePreviousEntry
        {
            CodeOrder = Math.Clamp(codeOrder, 1, 17),
            Year = year.Value,
            Month = month.Value,
            Received = map.TryGetValue("entry_received", out var receivedColumn) ? ParseMoney(Get(row, receivedColumn)) : 0m,
            Due = map.TryGetValue("entry_due", out var dueColumn) ? ParseMoney(Get(row, dueColumn)) : 0m
        };
    }

    private static void ApplyMilitarySnapshot(ExercisePreviousProcess p, ExercisePreviousMilitarySearchResult m)
    {
        p.MilitaryId = m.ActiveMilitaryId;
        p.Rank = Prefer(p.Rank, m.Rank);
        p.FullName = Prefer(p.FullName, m.FullName);
        p.WarName = Prefer(p.WarName, m.WarName);
        p.Cpf = Prefer(p.Cpf, m.Cpf);
        p.PrecCp = Prefer(p.PrecCp, m.PrecCp);
        p.Identity = Prefer(p.Identity, m.Identity);
        p.BirthDate = Prefer(p.BirthDate, m.BirthDate);
        p.Phone = Prefer(p.Phone, m.Phone);
        p.Bank = Prefer(p.Bank, m.Bank);
        p.Agency = Prefer(p.Agency, m.Agency);
        p.Account = Prefer(p.Account, m.Account);
    }

    private static bool HasMinimumData(ExercisePreviousProcess p)
        => !string.IsNullOrWhiteSpace(p.FullName) ||
           !string.IsNullOrWhiteSpace(p.Cpf) ||
           !string.IsNullOrWhiteSpace(p.PrecCp) ||
           !string.IsNullOrWhiteSpace(p.Identity) ||
           !string.IsNullOrWhiteSpace(p.ProcessNumber) ||
           !string.IsNullOrWhiteSpace(p.DebtType) ||
           !string.IsNullOrWhiteSpace(p.RequestedValue);

    private static ExercisePreviousImportIssue Issue(int rowNumber, string severity, string message, IReadOnlyList<string> row)
        => new() { RowNumber = rowNumber, Severity = severity, Message = message, RawData = string.Join(" | ", row.Select(Clean)) };

    private static string Prefer(string current, string databaseValue)
        => string.IsNullOrWhiteSpace(databaseValue) ? current : databaseValue;

    private static string Get(IReadOnlyList<string> row, int index)
        => index >= 0 && index < row.Count ? Clean(row[index]) : string.Empty;

    private static string Clean(string? value) => ExercisePreviousRepository.CleanDbText(value);
    private static string NormalizeHeader(string value) => ExercisePreviousRepository.LettersAndDigits(value);
    private static bool IsBlankRow(IEnumerable<string> row) => row.All(x => string.IsNullOrWhiteSpace(Clean(x)));

    private static string NormalizeCpf(string value) => ExercisePreviousRepository.FormatCpf(value);

    private static string NormalizeIdentifier(string value)
    {
        var text = Clean(value);
        var decimalZero = Regex.Match(text, @"^\s*(\d+)(?:[,.]0+)?\s*$");
        if (decimalZero.Success) text = decimalZero.Groups[1].Value;
        return ExercisePreviousRepository.LettersAndDigits(text);
    }

    private static string NormalizeDate(string value)
    {
        var text = Clean(value);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        if (TryExcelSerial(text, out var serialDate)) return serialDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy/MM/dd", "d MMM yy", "d MMM yyyy", "dd MMM yy", "dd MMM yyyy" };
        if (DateTime.TryParseExact(text, formats, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date) ||
            DateTime.TryParse(text, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return text;
    }

    private static string NormalizeMonth(string value)
    {
        var text = Clean(value);
        if (Regex.IsMatch(text, @"^\d{4}-\d{2}$")) return text;
        if (DateTime.TryParseExact(text, new[] { "MM/yyyy", "M/yyyy", "yyyy/MM", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" }, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date))
            return date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (TryExcelSerial(text, out date)) return date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return text;
    }

    private static bool TryExcelSerial(string text, out DateTime date)
    {
        date = default;
        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return false;
        if (value is < 20000 or > 80000) return false;
        date = DateTime.FromOADate(value);
        return true;
    }

    private static string NormalizeYesNo(string value)
    {
        var text = ExercisePreviousRepository.NormalizeSearchText(value);
        return text is "SIM" or "S" or "YES" or "Y" or "1" ? "Sim" : "Não";
    }

    private static int? TryInt(string value)
        => int.TryParse(ExercisePreviousRepository.Digits(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static (int? Year, int? Month) ParseCompetence(string value)
    {
        var text = Clean(value);
        var iso = Regex.Match(text, @"^(20\d{2}|19\d{2})[-/](\d{1,2})$");
        if (iso.Success) return (int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture));
        var br = Regex.Match(text, @"^(\d{1,2})[-/](20\d{2}|19\d{2})$");
        if (br.Success) return (int.Parse(br.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(br.Groups[1].Value, CultureInfo.InvariantCulture));
        var date = NormalizeDate(text);
        if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return (dt.Year, dt.Month);
        return (null, null);
    }

    private static int? ParseMonth(string value)
    {
        var text = ExercisePreviousRepository.NormalizeSearchText(value);
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 12) return number;
        var months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["JAN"] = 1, ["JANEIRO"] = 1, ["FEV"] = 2, ["FEVEREIRO"] = 2, ["MAR"] = 3, ["MARCO"] = 3,
            ["ABR"] = 4, ["ABRIL"] = 4, ["MAI"] = 5, ["MAIO"] = 5, ["JUN"] = 6, ["JUNHO"] = 6,
            ["JUL"] = 7, ["JULHO"] = 7, ["AGO"] = 8, ["AGOSTO"] = 8, ["SET"] = 9, ["SETEMBRO"] = 9,
            ["OUT"] = 10, ["OUTUBRO"] = 10, ["NOV"] = 11, ["NOVEMBRO"] = 11, ["DEZ"] = 12, ["DEZEMBRO"] = 12
        };
        return months.TryGetValue(text, out var month) ? month : null;
    }

    private static decimal ParseMoney(string value)
    {
        var text = Clean(value);
        if (string.IsNullOrWhiteSpace(text)) return 0m;
        text = text.Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (decimal.TryParse(text, NumberStyles.Currency, CultureInfo.GetCultureInfo("pt-BR"), out var br)) return br;
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var inv)) return inv;
        return 0m;
    }

    private static int? ResolveCodeOrder(string value, IReadOnlyList<ExercisePreviousCode> defaultCodes)
    {
        var digits = ExercisePreviousRepository.Digits(value);
        if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 17) return number;
        var normalized = ExercisePreviousRepository.NormalizeSearchText(value);
        foreach (var code in defaultCodes)
        {
            var description = ExercisePreviousRepository.NormalizeSearchText(code.Description);
            if (!string.IsNullOrWhiteSpace(description) && (description.Contains(normalized, StringComparison.Ordinal) || normalized.Contains(description, StringComparison.Ordinal)))
                return code.Order;
        }
        return null;
    }

    private static IEnumerable<ExercisePreviousCode> CloneCodes(IReadOnlyList<ExercisePreviousCode> codes)
    {
        if (codes.Count == 0)
        {
            for (var i = 1; i <= 17; i++) yield return new ExercisePreviousCode { Order = i };
            yield break;
        }
        foreach (var code in codes.OrderBy(x => x.Order).Take(17))
            yield return new ExercisePreviousCode { Order = code.Order, Description = code.Description, Type = code.Type };
    }

    private sealed record HeaderMatch(int RowIndex, Dictionary<string, int> ColumnMap, List<string> MappedHeaders, int Score);

    private static readonly Dictionary<string, string> HeaderAliases = BuildHeaderAliases();

    private static Dictionary<string, string> BuildHeaderAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add("rank", "Posto/Grad", "Posto Grad", "Posto", "P/G", "PG", "Graduacao", "Graduação");
        Add("full_name", "Nome", "Militar", "Nome Completo", "Integrante", "Nome do Militar");
        Add("war_name", "Nome de Guerra", "Nome Guerra", "NG", "Guerra");
        Add("cpf", "CPF", "Cpf");
        Add("prec_cp", "Prec", "Prec-CP", "PREC CP", "PREC_CP", "Preccp");
        Add("identity", "Identidade", "Idt", "RG", "Identidade Militar");
        Add("situation", "Situacao", "Situação", "Status");
        Add("bank", "Banco");
        Add("agency", "Agencia", "Agência");
        Add("account", "Conta", "Conta Corrente");
        Add("birth_date", "Nascimento", "Data Nascimento", "Data de Nascimento");
        Add("general_protocol", "Protocolo Geral", "Protocolo");
        Add("section", "Secao", "Seção");
        Add("subject_number", "Assunto Num", "Assunto Nº", "Numero Assunto");
        Add("subject_text", "Assunto", "Texto Assunto");
        Add("recipient", "Destinatario", "Destinatário");
        Add("object", "Objeto", "Objeto da Divida", "Objeto da Dívida");
        Add("phone", "Telefone", "Celular");
        Add("payment_reason", "Motivo", "Motivo Pagamento", "Justificativa");
        Add("requested_value", "Valor", "Valor Requerido", "Valor Solicitado");
        Add("process_number", "Processo", "Numero Processo", "Número Processo", "Num Processo");
        Add("process_year", "Ano", "Ano Processo");
        Add("request_date", "Data Requerimento", "Data da Solicitacao", "Data da Solicitação");
        Add("request_date_words", "Data por Extenso", "Data Solicitacao Extenso");
        Add("bulletin_number", "BI", "Boletim", "Numero BI", "Número BI", "BI Numero");
        Add("bulletin_date", "Data BI", "Data Boletim", "BI Data");
        Add("debt_type", "Especie Divida", "Espécie Dívida", "Tipo Divida", "Tipo Dívida");
        Add("period_start", "Inicio", "Início", "Periodo Inicio", "Período Início", "Data Inicio", "Data Início");
        Add("period_end", "Fim", "Periodo Fim", "Período Fim", "Data Fim");
        Add("updated_through", "Atualizado Ate", "Atualizado Até", "Atualizacao", "Atualização");
        Add("previous_exercise_type", "Tipo EA", "Tipo Exercicio Anterior", "Tipo Exercício Anterior", "Codigo EA", "Código EA");
        Add("ea_indicative", "Indicativo EA", "Indicativo");
        Add("has_judicial_pension", "Pensao Judiciaria", "Pensão Judiciária");
        Add("right_doc", "Documento Materializou", "Doc Materializou", "Materializacao", "Materialização");
        Add("bulletin_recorded", "Boletim Averbou", "BI Averbou");
        Add("non_payment", "Nao Pagamento", "Não Pagamento", "Explicacao Nao Pagamento", "Explicação Não Pagamento");
        Add("entry_code", "Codigo Lancamento", "Código Lançamento", "Codigo", "Código", "Rubrica");
        Add("entry_competence", "Competencia", "Competência", "Mes Ano", "Mês Ano");
        Add("entry_year", "Ano Lancamento", "Ano Lançamento");
        Add("entry_month", "Mes", "Mês", "Mes Lancamento", "Mês Lançamento");
        Add("entry_received", "Recebido", "Valor Recebido");
        Add("entry_due", "Devido", "Valor Devido", "Valor Total");
        return map;

        void Add(string canonical, params string[] aliases)
        {
            foreach (var alias in aliases)
                map[NormalizeHeader(alias)] = canonical;
        }
    }
}
