using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Controls;

namespace SIGFUR.Wpf.Services;

public sealed class JudicialPensionService
{
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly MilitaryRepository _military;
    private readonly LogService _log;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public JudicialPensionService(AppPaths paths, JsonFileService json, MilitaryRepository military, LogService log)
    {
        _paths = paths;
        _json = json;
        _military = military;
        _log = log;
        EnsureTemplateInstalled();
    }

    private void EnsureTemplateInstalled()
    {
        try
        {
            Directory.CreateDirectory(_paths.JudicialPensionTemplatesDirectory);
            var fileName = "PENSAO TEMPLATE.docx";
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Resources", "Pensao", fileName),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", "Pensao", fileName)),
                Path.Combine(AppContext.BaseDirectory, "templates", "docs", fileName)
            };
            var source = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(source)) return;
            var target = Path.Combine(_paths.JudicialPensionTemplatesDirectory, fileName);
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
                File.Copy(source, target, true);
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync("Falha ao preparar modelo de Pensão Judicial.", ex);
        }
    }

    public Task<JudicialPensionSettings?> LoadRawSettingsAsync() => _json.LoadAsync<JudicialPensionSettings>(_paths.JudicialPensionSettingsFile);
    public async Task<JudicialPensionSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
        => await _json.LoadAsync<JudicialPensionSettings>(_paths.JudicialPensionSettingsFile) ?? new JudicialPensionSettings();
    public Task SaveSettingsAsync(JudicialPensionSettings settings, CancellationToken cancellationToken = default)
        => _json.SaveAsync(_paths.JudicialPensionSettingsFile, settings);

    public static JudicialPensionCalculationResult Calculate(JudicialPensionCalculationInput input)
    {
        decimal Round(decimal value) => Math.Round(Math.Max(0m, value), 2, MidpointRounding.AwayFromZero);
        decimal Percent(decimal baseValue, decimal percent) => Round(baseValue * Math.Max(0m, percent) / 100m);

        var result = new JudicialPensionCalculationResult { Salary = Round(input.Salary) };
        result.QualificationAdditional = Percent(result.Salary, input.QualificationPercent);
        result.MilitaryAdditional = Percent(result.Salary, input.MilitaryAdditionalPercent);
        result.AvailabilityAdditional = Percent(result.Salary, input.AvailabilityPercent);
        result.PermanenceAdditional = Percent(result.Salary, input.PermanencePercent);
        result.Vencimentos = Round(result.Salary + result.QualificationAdditional + result.MilitaryAdditional + result.AvailabilityAdditional + result.PermanenceAdditional);

        var includedExtras = input.Extras.Where(x => x.IsIncluded).ToList();
        var extraEarnings = Round(includedExtras.Where(x => x.Type != "D").Sum(x => Math.Max(0m, x.Value)));
        var extraTaxableEarnings = Round(includedExtras.Where(x => x.Type != "D" && x.IsTaxable).Sum(x => Math.Max(0m, x.Value)));
        var extraDiscounts = Round(includedExtras.Where(x => x.Type == "D").Sum(x => Math.Max(0m, x.Value)));

        result.GrossEarnings = Round(result.Vencimentos + input.PreSchoolValue + input.FamilySalaryValue + extraEarnings);
        result.TaxableEarnings = Round(result.Vencimentos + extraTaxableEarnings);
        result.Fusex = Percent(result.Vencimentos, input.FusexPercent);
        result.MilitaryPension105 = Percent(result.Vencimentos, input.MilitaryPension105Percent);
        result.MilitaryPension15 = Percent(result.Vencimentos, input.MilitaryPension15Percent);
        result.Pnr = Percent(result.Salary, input.PnrPercent);

        var alimonyTotal = Round(input.ExistingAlimony + input.OtherAlimony);
        if (input.AutomaticIncomeTax)
        {
            var dependents = Round(Math.Max(0, input.IncomeTaxDependents) * AdjustmentAccountsService.IncomeTaxDependentDeduction2026);
            result.IncomeTaxBase = Round(
                result.TaxableEarnings
                - result.MilitaryPension105
                - result.MilitaryPension15
                - result.Fusex
                - (input.DeductMedicalFromIncomeTax ? input.FusexMedicalExpense : 0m)
                - (input.DeductFusexDependentFromIncomeTax ? input.FusexDependentDiscount : 0m)
                - (input.DeductAlimonyFromIncomeTax ? alimonyTotal : 0m)
                - (input.DeductPnrFromIncomeTax ? result.Pnr : 0m)
                - dependents);
            result.IncomeTaxBeforeReducer = Round(AdjustmentAccountsService.IncomeTaxTable2026(result.IncomeTaxBase));
            result.IncomeTaxReducer = input.ApplyIncomeTaxReducer2026
                ? Round(AdjustmentAccountsService.IncomeTaxReducer2026(result.TaxableEarnings, result.IncomeTaxBeforeReducer))
                : 0m;
            result.IncomeTax = Round(result.IncomeTaxBeforeReducer - result.IncomeTaxReducer);
        }
        else
        {
            result.IncomeTax = Round(input.ManualIncomeTax);
            result.IncomeTaxBase = 0m;
        }

        result.MandatoryDiscounts = Round(
            result.Fusex + result.MilitaryPension105 + result.MilitaryPension15
            + input.FusexDependentDiscount + input.FusexMedicalExpense + result.Pnr
            + alimonyTotal + result.IncomeTax + extraDiscounts);
        var netBase = Round(result.GrossEarnings - result.MandatoryDiscounts);
        result.PensionCalculationBase = input.PensionBase switch
        {
            "RECEITA" => result.GrossEarnings,
            "SOLDO" => result.Salary,
            _ => netBase
        };
        result.JudicialPension = input.FixedPension
            ? Round(input.FixedPensionValue)
            : Percent(result.PensionCalculationBase, input.PensionPercent);
        result.NetAfterPension = Round(result.GrossEarnings - result.MandatoryDiscounts - result.JudicialPension);

        result.EarningsDetail =
        [
            ("Soldo", result.Salary),
            ($"Adicional de Habilitação ({input.QualificationPercent:0.##}%)", result.QualificationAdditional),
            ($"Adicional Militar ({input.MilitaryAdditionalPercent:0.##}%)", result.MilitaryAdditional),
            ($"Ad C Disp Mil ({input.AvailabilityPercent:0.##}%)", result.AvailabilityAdditional),
            ($"Adicional de Permanência ({input.PermanencePercent:0.##}%)", result.PermanenceAdditional),
            ("Assistência Pré-Escolar", Round(input.PreSchoolValue)),
            ("Salário-Família", Round(input.FamilySalaryValue)),
            .. includedExtras.Where(x => x.Type != "D").Select(x => (x.Description, Round(x.Value)))
        ];
        result.DiscountsDetail =
        [
            ($"FUSEX ({input.FusexPercent:0.##}%)", result.Fusex),
            ($"Pensão Militar ({input.MilitaryPension105Percent:0.##}%)", result.MilitaryPension105),
            ($"Pensão Militar ({input.MilitaryPension15Percent:0.##}%)", result.MilitaryPension15),
            ("Desconto dependente FUSEX", Round(input.FusexDependentDiscount)),
            ("Despesa médica FUSEX", Round(input.FusexMedicalExpense)),
            ($"Ocupação PNR ({input.PnrPercent:0.##}%)", result.Pnr),
            ("Pensão alimentícia existente", Round(input.ExistingAlimony)),
            ("Outra pensão judicial", Round(input.OtherAlimony)),
            (input.AutomaticIncomeTax ? "Imposto de Renda automático" : "Imposto de Renda manual", result.IncomeTax),
            .. includedExtras.Where(x => x.Type == "D").Select(x => (x.Description, Round(x.Value)))
        ];

        var m = (decimal value) => MilitaryFormatting.FormatMoney((double)value);
        var details = new StringBuilder();
        details.AppendLine("CÁLCULO DE PENSÃO JUDICIAL — ESTIMATIVA ADMINISTRATIVA");
        details.AppendLine();
        details.AppendLine("RECEITAS");
        foreach (var line in result.EarningsDetail.Where(x => x.Value != 0m)) details.AppendLine($"  {line.Description}: {m(line.Value)}");
        details.AppendLine($"  TOTAL RECEITAS: {m(result.GrossEarnings)}");
        details.AppendLine();
        details.AppendLine("DESCONTOS OBRIGATÓRIOS");
        foreach (var line in result.DiscountsDetail.Where(x => x.Value != 0m)) details.AppendLine($"  {line.Description}: {m(line.Value)}");
        if (input.AutomaticIncomeTax)
        {
            details.AppendLine($"  Base do IR: {m(result.IncomeTaxBase)}");
            if (result.IncomeTaxReducer > 0) details.AppendLine($"  Redutor IR 2026: -{m(result.IncomeTaxReducer)}");
        }
        details.AppendLine($"  TOTAL DESCONTOS: {m(result.MandatoryDiscounts)}");
        details.AppendLine();
        details.AppendLine($"BASE ESCOLHIDA PARA A PENSÃO: {m(result.PensionCalculationBase)}");
        details.AppendLine($"PENSÃO JUDICIAL: {m(result.JudicialPension)}");
        details.AppendLine($"LÍQUIDO APÓS A PENSÃO: {m(result.NetAfterPension)}");
        details.AppendLine();
        details.AppendLine("Atenção: confira o título judicial, as rubricas efetivas do contracheque e as regras tributárias aplicáveis ao caso concreto.");
        result.Summary = details.ToString();
        return result;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _military.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 12
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=12000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            await _military.EnsureSchemaAsync(cancellationToken);
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS pensao_judicial_calculos(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    militar_id INTEGER,
                    nome_militar TEXT DEFAULT '',
                    posto TEXT DEFAULT '',
                    soldo REAL NOT NULL DEFAULT 0,
                    valor_pensao REAL NOT NULL DEFAULT 0,
                    percentual_pensao REAL NOT NULL DEFAULT 0,
                    base_calculo TEXT DEFAULT '',
                    detalhes_json TEXT DEFAULT '',
                    criado_em TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaReady = true;
        }
        finally { _schemaGate.Release(); }
    }

    public async Task<int> SaveCalculationAsync(
        MilitaryRecord? military,
        JudicialPensionSettings settings,
        JudicialPensionCalculationResult result,
        bool updateMilitaryAlimony,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO pensao_judicial_calculos(militar_id,nome_militar,posto,soldo,valor_pensao,percentual_pensao,base_calculo,detalhes_json,criado_em)
            VALUES($id,$nome,$posto,$soldo,$valor,$percentual,$base,$json,$at);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$id", (object?)military?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$nome", military?.Name ?? string.Empty);
        command.Parameters.AddWithValue("$posto", military?.Rank ?? settings.Rank);
        command.Parameters.AddWithValue("$soldo", (double)result.Salary);
        command.Parameters.AddWithValue("$valor", (double)result.JudicialPension);
        command.Parameters.AddWithValue("$percentual", (double)(settings.FixedPension ? 0m : settings.PensionPercent));
        command.Parameters.AddWithValue("$base", settings.PensionBase);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new { Settings = settings, Result = result }));
        command.Parameters.AddWithValue("$at", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
        var savedId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        if (updateMilitaryAlimony && military is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE militares SET pensao_alimenticia='Sim', valor_pensao=$value WHERE id=$id;";
            update.Parameters.AddWithValue("$value", result.JudicialPension.ToString("0.00", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", military.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return savedId;
    }

    public async Task<List<JudicialPensionHistoryRecord>> ListHistoryAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<JudicialPensionHistoryRecord>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,militar_id,nome_militar,posto,soldo,valor_pensao,percentual_pensao,base_calculo,criado_em FROM pensao_judicial_calculos ORDER BY id DESC LIMIT 100;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DateTime.TryParse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var created);
            result.Add(new JudicialPensionHistoryRecord
            {
                Id = reader.GetInt32(0),
                MilitaryId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                MilitaryName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Rank = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Salary = ToDecimal(reader.GetValue(4)),
                PensionValue = ToDecimal(reader.GetValue(5)),
                PensionPercent = ToDecimal(reader.GetValue(6)),
                CalculationBase = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                CreatedAt = created
            });
        }
        return result;

        static decimal ToDecimal(object value)
        {
            if (value is null || value is DBNull) return 0m;
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
    }

    public static string BuildBulletinText(MilitaryRecord? military, JudicialPensionSettings settings, JudicialPensionCalculationResult result)
    {
        var b = settings.Bulletin;
        var mode = NormalizeMode(b.Mode);
        var rank = military?.ShortRank ?? MilitaryRankService.ShortName(settings.Rank);
        var displayName = military is null
            ? "[MILITAR]"
            : NameHighlightHelper.PlainDisplay(military.Name, military.WarName).ToUpperInvariant();
        var prec = military?.PrecCp ?? "[PREC-CP]";
        var cpf = military?.FormattedCpf ?? "[CPF]";
        var pensionValue = MilitaryFormatting.FormatMoney((double)result.JudicialPension);
        var arrearsValue = string.IsNullOrWhiteSpace(b.ArrearsTotalValue) ? pensionValue : b.ArrearsTotalValue.Trim();
        var percent = settings.FixedPension ? "valor fixo" : $"{settings.PensionPercent:0.##}%".Replace('.', ',');
        var legalBasis = string.IsNullOrWhiteSpace(b.LegalBasis)
            ? "Art. 529 da Lei nº 13.105/2015 (Código de Processo Civil), Lei nº 5.478/1968 e arts. 14 e 15, inciso VI, da Medida Provisória nº 2.215-10/2001"
            : b.LegalBasis.Trim().TrimEnd('.');

        static string Required(string? text) => string.IsNullOrWhiteSpace(text) ? "[PREENCHER]" : text.Trim();
        var sb = new StringBuilder();
        sb.AppendLine($"Com fundamento no {legalBasis}, e em cumprimento à {Required(b.DecisionType)} prolatada em {Required(b.DecisionDate)}, pela {Required(b.DecisionOrigin)}, processo nº {Required(b.ProcessNumber)}, natureza da ação {Required(b.ActionNature)}, {OpeningText(mode)}.");
        sb.AppendLine();
        sb.AppendLine("DADOS DO MILITAR / ALIMENTANTE");
        sb.AppendLine();
        sb.AppendLine($"{rank} {displayName}".Trim());
        sb.AppendLine($"Prec-CP {MilitaryFormatting.Digits(prec)} CPF {cpf}".Trim());
        sb.AppendLine();

        if (mode is "IMPLANTAÇÃO")
        {
            sb.AppendLine("DADOS DO PROCESSO");
            sb.AppendLine($"Número do processo: {Required(b.ProcessNumber)}.");
            sb.AppendLine($"Origem da decisão: {Required(b.DecisionOrigin)} | Localidade: {Required(b.Locality)} | Tribunal: {Required(b.Court)}.");
            sb.AppendLine($"Decisão judicial: {Required(b.DecisionType)} | Data da decisão: {Required(b.DecisionDate)} | Natureza da ação: {Required(b.ActionNature)}.");
            sb.AppendLine($"Resumo da decisão: {Required(b.DecisionSummary)}.");
            sb.AppendLine($"Autor(a): {Required(b.PlaintiffName)}, CPF {Required(b.PlaintiffCpf)}.");
            sb.AppendLine($"Réu/alimentante: {rank} {displayName}, CPF {cpf}.");
            sb.AppendLine($"Ofício de recebimento: {Required(b.ReceivingLetter)}.");
            sb.AppendLine();
            sb.AppendLine("DADOS DA IMPLANTAÇÃO DA PENSÃO");
            sb.AppendLine($"Alimentado(a): {Required(b.BeneficiaryName)}, CPF {Required(b.BeneficiaryCpf)}.");
            sb.AppendLine($"Detentor(a) da guarda legal: {Required(b.GuardianName)}, CPF {Required(b.GuardianCpf)}.");
            sb.AppendLine($"Grau de parentesco: {Required(b.Relationship)} | Tipo de pensão: {Required(b.PensionType)}.");
            sb.AppendLine(settings.FixedPension
                ? $"Regra/valor da pensão: {Required(b.CalculationRule)} | Valor mensal: {pensionValue}."
                : $"Regra/valor da pensão: {Required(b.CalculationRule)} | Percentual: {percent} sobre {BaseLabel(settings.PensionBase)} | Valor estimado: {pensionValue}.");
            sb.AppendLine($"Incidências: férias — {Required(b.VacationIncidence)}; adicional natalino — {Required(b.ChristmasIncidence)}; compensação pecuniária — {Required(b.CompensationIncidence)}.");
            sb.AppendLine($"Pensões anteriores/base de cálculo: {Required(b.PreviousPensions)}.");
            sb.AppendLine();
            AppendBank(sb, b);
        }
        else if (mode is "ATUALIZAÇÃO")
        {
            sb.AppendLine("DADOS PARA ATUALIZAÇÃO");
            sb.AppendLine($"Item/motivo da atualização: {Required(b.Observation)}.");
            sb.AppendLine($"Resumo da decisão/ofício: {Required(b.DecisionSummary)}.");
            sb.AppendLine($"Alimentado(a): {Required(b.BeneficiaryName)}, CPF {Required(b.BeneficiaryCpf)}.");
            sb.AppendLine($"Detentor(a)/representante: {Required(b.GuardianName)}, CPF {Required(b.GuardianCpf)}.");
            sb.AppendLine($"Regra/valor atualizado: {Required(b.CalculationRule)} | Valor estimado: {pensionValue}.");
        }
        else if (mode is "DESCONTO DE ATRASADOS")
        {
            sb.AppendLine("DADOS DO DESCONTO DE ATRASADOS");
            sb.AppendLine($"Valor total do desconto: {Required(arrearsValue)}.");
            sb.AppendLine($"Mês/Ano de referência: {Required(FormatReference(b.ReferenceMonth, b.ReferenceYear))}.");
            sb.AppendLine($"Alimentado(a): {Required(b.BeneficiaryName)}, CPF {Required(b.BeneficiaryCpf)}.");
            sb.AppendLine($"Representante/detentor da guarda: {Required(b.GuardianName)}, CPF {Required(b.GuardianCpf)}.");
        }
        else
        {
            sb.AppendLine("DADOS DA EXCLUSÃO");
            sb.AppendLine($"Número do processo implantado no SIPPES: {Required(b.ProcessNumber)}.");
            sb.AppendLine($"Alimentado(a): {Required(b.BeneficiaryName)}, CPF {Required(b.BeneficiaryCpf)}.");
            sb.AppendLine($"Detentor(a) da guarda legal: {Required(b.GuardianName)}, CPF {Required(b.GuardianCpf)}.");
            sb.AppendLine($"Motivo da exclusão: {Required(b.ExclusionReason)}.");
            sb.AppendLine($"Justificativa da exclusão: {Required(b.ExclusionJustification)}.");
            sb.AppendLine($"Data de encerramento da pensão: {Required(b.PensionEndDate)}.");
        }

        if (!string.IsNullOrWhiteSpace(b.Representation)) sb.AppendLine($"Representação/observação complementar: {b.Representation.Trim()}.");
        return sb.ToString().Trim();

        static string OpeningText(string mode) => mode switch
        {
            "ATUALIZAÇÃO" => "seja atualizada a Pensão Judicial do militar abaixo identificado no SIPPES",
            "DESCONTO DE ATRASADOS" => "seja realizado o desconto de atrasados de Pensão Judicial do militar abaixo identificado no SIPPES",
            "EXCLUSÃO" => "seja excluída a Pensão Judicial do militar abaixo identificado no SIPPES",
            _ => "seja implantada a Pensão Judicial na remuneração do militar abaixo identificado no SIPPES"
        };

        static void AppendBank(StringBuilder sb, JudicialPensionBulletin b)
        {
            sb.AppendLine("DOMICÍLIO BANCÁRIO DO DETENTOR/BENEFICIÁRIO");
            var isCaixa = b.BankCode.Contains("104", StringComparison.OrdinalIgnoreCase) || b.BankName.Contains("Caixa", StringComparison.OrdinalIgnoreCase);
            var operation = isCaixa ? Required(b.BankOperation) : (string.IsNullOrWhiteSpace(b.BankOperation) ? "não se aplica" : b.BankOperation.Trim());
            sb.AppendLine($"Banco {Required(b.BankCode)} — {Required(b.BankName)}, Agência {Required(b.Agency)}, Conta Corrente {Required(b.Account)}, Operação CAIXA: {operation}.");
            sb.AppendLine($"Rubrica SIPPES: {Required(b.Rubric)}.");
        }
    }

    private static string NormalizeMode(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        return text.Contains("DESCONTO") || text.Contains("ATRAS") ? "DESCONTO DE ATRASADOS"
            : text.Contains("EXCLU") ? "EXCLUSÃO"
            : text.Contains("ATUAL") ? "ATUALIZAÇÃO"
            : "IMPLANTAÇÃO";
    }

    private static string FormatReference(string? month, string? year)
    {
        var m = (month ?? string.Empty).Trim();
        var y = (year ?? string.Empty).Trim();
        return string.Join("/", new[] { m, y }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string BaseLabel(string value) => value switch { "RECEITA" => "total das receitas", "SOLDO" => "soldo", _ => "receitas menos descontos obrigatórios" };

    public async Task ExportDocxAsync(
        string path,
        MilitaryRecord? military,
        JudicialPensionSettings settings,
        JudicialPensionCalculationResult result,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _paths.JudicialPensionOutputDirectory);
        if (await TryExportFromTemplateAsync(path, military, settings, result, cancellationToken)) return;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, "[Content_Types].xml", ContentTypes, cancellationToken);
                await WriteEntryAsync(archive, "_rels/.rels", RootRels, cancellationToken);
                await WriteEntryAsync(archive, "word/document.xml", BuildDocumentXml(military, settings, result), cancellationToken);
                await WriteEntryAsync(archive, "word/_rels/document.xml.rels", DocumentRels, cancellationToken);
                await WriteEntryAsync(archive, "word/styles.xml", WordStyles, cancellationToken);
            }
            using (var archive = ZipFile.OpenRead(temp))
            {
                foreach (var xml in archive.Entries.Where(x => x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || x.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
                {
                    using var stream = xml.Open();
                    using var reader = XmlReader.Create(stream);
                    while (reader.Read()) { }
                }
            }
            File.Move(temp, path, true);
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha gerando documento de Pensão Judicial.", ex);
            throw;
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }


    private async Task<bool> TryExportFromTemplateAsync(string path, MilitaryRecord? military, JudicialPensionSettings settings, JudicialPensionCalculationResult result, CancellationToken cancellationToken)
    {
        var template = Path.Combine(_paths.JudicialPensionTemplatesDirectory, "PENSAO TEMPLATE.docx");
        if (!File.Exists(template)) return false;
        try
        {
            await RenderTemplateAsync(template, path, BuildTemplateMap(military, settings, result), cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao preencher PENSAO TEMPLATE.docx; usando geração nativa como fallback.", ex);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return false;
        }
    }

    private static Dictionary<string, string> BuildTemplateMap(MilitaryRecord? military, JudicialPensionSettings settings, JudicialPensionCalculationResult result)
    {
        var b = settings.Bulletin;
        static string M(decimal value) => value == 0m ? string.Empty : value.ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
        static string S(string? value) => value?.Trim() ?? string.Empty;
        var rank = military?.ShortRank ?? MilitaryRankService.ShortName(settings.Rank);
        var alimentante = military is null ? string.Empty : NameHighlightHelper.PlainDisplay(military.Name, military.WarName).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        var alimentanteCpf = military?.FormattedCpf ?? string.Empty;
        var bank = string.Join(" - ", new[] { S(b.BankCode), S(b.BankName) }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PREC_CP"] = military?.PrecCp ?? string.Empty,
            ["PREC-CP"] = military?.PrecCp ?? string.Empty,
            ["CPF"] = alimentanteCpf,
            ["POSTO_GRAD"] = rank,
            ["POSTO"] = rank,
            ["POSTO_ABREV"] = rank,
            ["NOME"] = alimentante,
            ["NOME_COMPLETO"] = alimentante,
            ["MILITAR_NOME"] = alimentante,
            ["MILITAR_PG_ABREV"] = rank,
            ["MILITAR_PREC_CP"] = military?.PrecCp ?? string.Empty,
            ["MILITAR_IDT"] = military?.MilitaryId ?? string.Empty,
            ["ALIMENTANTE_NOME"] = alimentante,
            ["ALIMENTANTE_CPF"] = alimentanteCpf,
            ["X_IMPLANTACAO"] = NormalizeMode(b.Mode) == "IMPLANTAÇÃO" ? "X" : string.Empty,
            ["X_MODIFICACAO"] = NormalizeMode(b.Mode) == "IMPLANTAÇÃO" ? string.Empty : "X",
            ["ALIMENTADO_NOME"] = S(b.BeneficiaryName).ToUpper(CultureInfo.GetCultureInfo("pt-BR")),
            ["ALIMENTADO_CPF"] = S(b.BeneficiaryCpf),
            ["BANCO"] = bank,
            ["AGENCIA"] = S(b.Agency),
            ["CONTA"] = S(b.Account),
            ["SOLDO"] = M(result.Salary),
            ["AD_C_DISP_MIL"] = M(result.AvailabilityAdditional),
            ["ADIC_HAB"] = M(result.QualificationAdditional),
            ["ADIC_MIL"] = M(result.MilitaryAdditional),
            ["AD_PERM"] = M(result.PermanenceAdditional),
            ["HVOO"] = string.Empty,
            ["GRAT_LOC_ESP"] = settings.Extras.FirstOrDefault(x => x.IsIncluded && x.Type != "D" && x.Description.Contains("localidade", StringComparison.OrdinalIgnoreCase)) is { } loc ? M(loc.Value) : string.Empty,
            ["SOMA_A"] = M(result.GrossEarnings),
            ["FUSEX_3"] = M(result.Fusex),
            ["DESC_DEP_FUSEX"] = M(settings.FusexDependentDiscount),
            ["P_MIL_15"] = M(result.MilitaryPension15),
            ["PENS_MIL_105"] = M(result.MilitaryPension105),
            ["IR_Z10"] = M(result.IncomeTax),
            ["PNR_106"] = M(result.Pnr),
            ["SOLDO_DESP_MED"] = M(settings.FusexMedicalExpense),
            ["PJ_1"] = M(settings.ExistingAlimony),
            ["PJ_2"] = M(settings.OtherAlimony),
            ["SOMA_B"] = M(result.MandatoryDiscounts),
            ["VENCIMENTOS"] = M(result.Vencimentos),
            ["DESCONTOS_OBR"] = M(result.MandatoryDiscounts),
            ["BASE_CALCULO"] = M(result.PensionCalculationBase),
            ["PERCENTUAL_PJ"] = settings.FixedPension ? "VALOR FIXO" : settings.PensionPercent.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) + "%",
            ["VALOR_PENSAO"] = M(result.JudicialPension),
            ["SALARIO_FAMILIA"] = M(settings.FamilySalaryValue),
            ["MES_ANO"] = FormatReference(b.ReferenceMonth, b.ReferenceYear),
            ["IR_REMUN"] = M(result.TaxableEarnings),
            ["IR_DESC_OBR"] = M(result.MilitaryPension105 + result.MilitaryPension15 + result.Fusex + settings.FusexDependentDiscount + settings.FusexMedicalExpense + result.Pnr + settings.ExistingAlimony + settings.OtherAlimony),
            ["IR_DEP_QTD"] = settings.IncomeTaxDependents <= 0 ? string.Empty : settings.IncomeTaxDependents.ToString(CultureInfo.InvariantCulture),
            ["IR_DEP"] = settings.IncomeTaxDependents <= 0 ? string.Empty : M(settings.IncomeTaxDependents * AdjustmentAccountsService.IncomeTaxDependentDeduction2026),
            ["IR_BASE"] = M(result.IncomeTaxBase),
            ["IR_ALIQ"] = result.IncomeTaxBase <= 0 ? string.Empty : "conforme tabela vigente",
            ["IR_TOTAL"] = M(result.IncomeTax),
            ["ASS_NOME"] = string.Empty,
            ["ASS_NOME_GUERRA"] = string.Empty,
            ["ASS_POSTO_ABREV"] = string.Empty
        };

        // Alias adicionais para modelos trazidos do Word antigo. Evita sair nome vazio
        // quando o template usa NOME/MILITAR_NOME em vez de ALIMENTANTE_NOME.
        map["REU_NOME"] = alimentante;
        map["REU_CPF"] = alimentanteCpf;
        map["BENEFICIARIO_NOME"] = map.GetValueOrDefault("ALIMENTADO_NOME", string.Empty);
        map["BENEFICIARIO_CPF"] = map.GetValueOrDefault("ALIMENTADO_CPF", string.Empty);
        return map;
    }

    private static async Task RenderTemplateAsync(string template, string destination, IReadOnlyDictionary<string, string> mapping, CancellationToken cancellationToken)
    {
        File.Copy(template, destination, true);
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Update);
        foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || x.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string xml;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, 4096, false))
                xml = await reader.ReadToEndAsync(cancellationToken);
            var patched = ReplaceTemplateTokens(xml, mapping);
            if (patched == xml) continue;
            using var stream = entry.Open();
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, false);
            await writer.WriteAsync(patched.AsMemory(), cancellationToken);
        }
    }

    private static string ReplaceTemplateTokens(string xml, IReadOnlyDictionary<string, string> mapping)
    {
        var text = xml;
        foreach (var pair in mapping.OrderByDescending(x => x.Key.Length))
        {
            var key = pair.Key.Trim();
            var value = SecurityElement.Escape(pair.Value ?? string.Empty) ?? string.Empty;
            foreach (var token in new[] { "{{" + key + "}}", "[[" + key + "]]", "<<" + key + ">>", "$" + key + "$", "{" + key + "}" })
            {
                text = text.Replace(token, value, StringComparison.OrdinalIgnoreCase);
                try
                {
                    var split = string.Join("(?:<[^>]+>)*", token.Select(ch => Regex.Escape(ch.ToString())));
                    text = Regex.Replace(text, split, _ => value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
                catch { }
            }
            try
            {
                text = Regex.Replace(text, "\\{\\s*" + Regex.Escape(key) + "\\s*\\}", value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch { }
        }
        return text;
    }

    private static string BuildDocumentXml(MilitaryRecord? military, JudicialPensionSettings settings, JudicialPensionCalculationResult result)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        Paragraph("PENSÃO JUDICIAL — MEMÓRIA DE CÁLCULO", false, "center");
        Paragraph($"Militar: {(military is null ? settings.Rank : military.ShortRank)} {(military?.Name ?? string.Empty)}", false, "left", military is not null);
        if (military is not null) Paragraph($"PREC-CP: {military.PrecCp}   CPF: {military.FormattedCpf}", false, "left");
        Paragraph($"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm}", false, "left");
        Paragraph("", false, "left");
        foreach (var line in result.Summary.Split('\n')) Paragraph(line.TrimEnd('\r'), false, "left");
        Paragraph("", false, "left");
        Paragraph("TEXTO PARA BOLETIM", false, "left");
        foreach (var line in BuildBulletinText(military, settings, result).Split('\n')) Paragraph(line.TrimEnd('\r'), false, "both", military is not null);
        sb.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\"/></w:sectPr></w:body></w:document>");
        return sb.ToString();

        void Paragraph(string text, bool bold, string align, bool highlightWarName = false)
        {
            sb.Append("<w:p><w:pPr><w:jc w:val=\"").Append(align).Append("\"/><w:spacing w:after=\"80\"/></w:pPr>");
            var ranges = highlightWarName && military is not null
                ? BulletinTextFormatter.FindWarNameRanges(text, new[] { military })
                : [];
            var cursor = 0;
            foreach (var range in ranges)
            {
                Run(text[cursor..range.Start], bold);
                Run(text.Substring(range.Start, range.Length), true);
                cursor = range.Start + range.Length;
            }
            if (cursor < text.Length) Run(text[cursor..], bold);
            if (text.Length == 0) Run(string.Empty, bold);
            sb.Append("</w:p>");

            void Run(string value, bool runBold)
            {
                sb.Append("<w:r><w:rPr><w:rFonts w:ascii=\"Times New Roman\" w:hAnsi=\"Times New Roman\"/><w:sz w:val=\"22\"/>");
                sb.Append(runBold ? "<w:b/>" : "<w:b w:val=\"0\"/>");
                sb.Append("</w:rPr><w:t xml:space=\"preserve\">").Append(Escape(value)).Append("</w:t></w:r>");
            }
        }
    }

    private static string Escape(string? value)
    {
        var safe = new string((value ?? string.Empty).Where(XmlConvert.IsXmlChar).ToArray());
        return SecurityElement.Escape(safe) ?? string.Empty;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string text, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private const string ContentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>";
    private const string RootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
    private const string DocumentRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";
    private const string WordStyles = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:rPr><w:rFonts w:ascii=\"Times New Roman\" w:hAnsi=\"Times New Roman\"/><w:sz w:val=\"22\"/></w:rPr></w:style></w:styles>";
}
