using System.Security;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class MilitaryDocumentGenerationService
{
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly MilitaryRepository _repository;

    public MilitaryDocumentGenerationService(AppPaths paths, SettingsService settings, LogService log, MilitaryRepository repository)
    {
        _paths = paths;
        _settings = settings;
        _log = log;
        _repository = repository;
    }

    public async Task<DocumentGenerationResult> GenerateAsync(
        DocumentGenerationRequest request,
        IProgress<(int Current, int Total, string Name)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new DocumentGenerationResult();
        var rows = request.Military?.Where(x => x is not null).DistinctBy(x => x.Id).ToList() ?? [];
        if (rows.Count == 0)
        {
            result.Failures.Add("Nenhum militar foi selecionado para a geração.");
            return result;
        }

        var output = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? Path.Combine(_paths.GeneratedDocumentsDirectory, TypeSlug(request.Type), DateTime.Now.ToString("yyyyMMdd_HHmmss"))
            : request.OutputDirectory;
        Directory.CreateDirectory(output);

        var template = !string.IsNullOrWhiteSpace(request.TemplatePath) && File.Exists(request.TemplatePath)
            ? request.TemplatePath
            : await ResolveTemplateAsync(request.Type);
        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var military = rows[index];
            progress?.Report((index + 1, rows.Count, military.Name));
            try
            {
                var effectiveFields = new Dictionary<string, string>(request.Fields ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                if (request.Type == GeneratedDocumentType.TransportAid)
                {
                    var transportDefaults = await GetTransportDefaultsAsync(
                        military,
                        effectiveFields,
                        request.UseTransportAddressFromDatabase,
                        request.UseTransportBusesFromDatabase,
                        cancellationToken);
                    foreach (var pair in transportDefaults) effectiveFields[pair.Key] = pair.Value;
                }
                var mapping = BuildMapping(request.Type, military, effectiveFields);
                var file = await GenerateOneAsync(request.Type, military, mapping, output, template, cancellationToken);
                result.Files.Add(file);
            }
            catch (Exception ex)
            {
                result.Failures.Add($"{military.ShortRank} {military.Name}: {ex.Message}");
                await _log.WriteAsync($"Falha gerando {request.Type} para {military.Name}.", ex);
            }
        }

        if (result.Failures.Count > 0)
        {
            var report = Path.Combine(output, "FALHAS_GERACAO.txt");
            await File.WriteAllLinesAsync(report, result.Failures, Encoding.UTF8, cancellationToken);
        }

        if (request.OpenAfterGenerate && result.Files.Count > 0)
        {
            // Um documento: abre o próprio arquivo. Em lote, abre a pasta para não
            // disparar dezenas de aplicativos ao mesmo tempo.
            ShellService.OpenPath(result.Files.Count == 1 ? result.Files[0] : output);
        }
        return result;
    }

    public async Task<string?> ResolveTemplateAsync(GeneratedDocumentType type)
    {
        var profile = await _settings.LoadProfileAsync();
        var names = TemplateNames(type);
        var roots = new List<string>
        {
            _paths.DocumentTemplatesDirectory,
            Path.Combine(AppContext.BaseDirectory, "Resources", "Documentos", "Listar"),
            Path.Combine(AppContext.BaseDirectory, "templates", "docs"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "EA"),
            _paths.PhpmTemplatesDirectory,
            _paths.ExercisePreviousTemplatesDirectory,
            _paths.GratificationTemplatesDirectory,
            AppContext.BaseDirectory,
        };
        if (!string.IsNullOrWhiteSpace(profile.LegacyProjectRoot))
        {
            roots.Add(profile.LegacyProjectRoot);
            roots.Add(Path.Combine(profile.LegacyProjectRoot, "templates", "docs"));
            roots.Add(Path.Combine(profile.LegacyProjectRoot, "interface"));
        }

        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in names)
            {
                var direct = Path.Combine(root, name);
                if (File.Exists(direct)) return direct;
            }
            try
            {
                var normalizedNames = names.Select(NormalizeFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var match = Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(x => normalizedNames.Contains(NormalizeFileName(Path.GetFileName(x))));
                if (!string.IsNullOrWhiteSpace(match)) return match;
            }
            catch { }
        }
        return null;
    }

    public static string DisplayName(GeneratedDocumentType type) => type switch
    {
        GeneratedDocumentType.TransportAid => "Auxílio-Transporte",
        GeneratedDocumentType.PecuniaryCompensation => "Pecuniária",
        GeneratedDocumentType.AuthenticPaymentCopy => "Cópia autêntica de pagamento",
        GeneratedDocumentType.AdvanceChristmasBonus => "Adiantamento natalino",
        GeneratedDocumentType.PostalLabel => "Correios / etiqueta",
        GeneratedDocumentType.CoverSheet => "Capa do processo",
        GeneratedDocumentType.ExercisePreviousRequest => "Requerimento / Informação EA",
        GeneratedDocumentType.JudicialPensionWorksheet => "Pensão Judicial — ficha-controle",
        GeneratedDocumentType.RemissiveIndex => "Índice remissivo",
        GeneratedDocumentType.GratificationDiex => "Grat Rep — DIEx",
        GeneratedDocumentType.GratificationMap => "Grat Rep — mapa",
        _ => "Documento"
    };

    public static string TypeSlug(GeneratedDocumentType type) => type switch
    {
        GeneratedDocumentType.TransportAid => "auxilio_transporte",
        GeneratedDocumentType.PecuniaryCompensation => "pecuniaria",
        GeneratedDocumentType.AuthenticPaymentCopy => "copia_autentica_pagamento",
        GeneratedDocumentType.AdvanceChristmasBonus => "adiantamento_natalino",
        GeneratedDocumentType.PostalLabel => "correios",
        GeneratedDocumentType.CoverSheet => "capa_processo",
        GeneratedDocumentType.ExercisePreviousRequest => "requerimento_ea",
        GeneratedDocumentType.JudicialPensionWorksheet => "pensao_judicial_ficha",
        GeneratedDocumentType.RemissiveIndex => "indice_remissivo",
        GeneratedDocumentType.GratificationDiex => "grat_rep_diex",
        GeneratedDocumentType.GratificationMap => "grat_rep_mapa",
        _ => "documento"
    };

    public static IReadOnlyList<(string Key, string Label, string Default)> FieldsFor(GeneratedDocumentType type)
    {
        var today = DateTime.Today.ToString("dd/MM/yyyy");
        var todayLong = DateTime.Today.ToString("dd 'de' MMMM 'de' yyyy", CultureInfo.GetCultureInfo("pt-BR"));
        return type switch
        {
            GeneratedDocumentType.TransportAid =>
            [
                ("CIDADE_UF", "Cidade / UF", "Belo Horizonte - MG"),
                ("DATA_REQUERIMENTO", "Data do requerimento", today),
                ("MES", "Mês da competência", DateTime.Today.ToString("MMMM", CultureInfo.GetCultureInfo("pt-BR")).ToUpperInvariant()),
                ("ANO", "Ano da competência", DateTime.Today.Year.ToString()),
                ("DIAS_UTEIS", "Dias úteis", "22"),
                ("CMD_NOME_POSTO", "Comandante no parecer", ""),
                ("OM_NOME", "Nome da OM", "4ª Companhia de Polícia do Exército"),
                ("CMT_OM", "Cmt da OM / autoridade", "Cmt da 4ª Companhia de Polícia do Exército"),
                ("SEJA_CONCEDIDO", "Texto do parecer", "SEJA")
            ],
            GeneratedDocumentType.PecuniaryCompensation =>
            [
                ("CIDADE_UF", "Cidade / UF", "Belo Horizonte - MG"),
                ("DATA_REQUERIMENTO", "Data do requerimento", today),
                ("DATA_COMPLETO", "Data de conclusão / licenciamento", ""),
                ("BI_LICENCIAMENTO", "BI de licenciamento", ""),
                ("DATA_BI_LIC", "Data do BI de licenciamento", ""),
                ("ORDENADOR", "Ordenador de Despesas — nome editável", "")
            ],
            GeneratedDocumentType.PostalLabel =>
            [
                ("DIEX_NUMERO", "Número do DIEx", ""),
                ("DIEX_DATA_EXTENSO", "Data do DIEx", today),
                ("DESTINATARIO", "Destinatário", ""),
                ("OM_DESTINO", "OM de destino", ""),
                ("LOGRADOURO_DESTINO", "Logradouro de destino", ""),
                ("NUMERO_DESTINO", "Número", ""),
                ("COMPLEMENTO_DESTINO", "Complemento", ""),
                ("BAIRRO_DESTINO", "Bairro", ""),
                ("ENDERECO_DESTINO", "Endereço de destino", ""),
                ("CIDADE_DESTINO", "Cidade de destino", ""),
                ("UF_DESTINO", "UF de destino", ""),
                ("CEP_DESTINO", "CEP de destino", ""),
                ("REMETENTE_NOME", "Remetente", "Cmt da 4ª Companhia de Polícia do Exército"),
                ("OM_NOME", "Nome da OM remetente", "4ª Companhia de Polícia do Exército"),
                ("CMT_OM", "Cmt da OM remetente", "Cmt da 4ª Companhia de Polícia do Exército"),
                ("ORIGEM_ENDERECO", "Endereço de origem", "Rua Juiz de Fora, 990 - Barro Preto"),
                ("ORIGEM_CIDADE_UF", "Cidade / UF de origem", "Belo Horizonte - MG"),
                ("ORIGEM_CEP", "CEP de origem", "30180-060")
            ],
            GeneratedDocumentType.AuthenticPaymentCopy =>
            [
                ("BAR_NUMERO", "Número do BAR", ""),
                ("BAR_DATA", "Data do BAR", today),
                ("VALOR", "Valor do pagamento", ""),
                ("REFERENCIA_VENCIMENTOS", "Referência dos vencimentos", ""),
                ("ORGAO_PAGADOR", "Órgão pagador", ""),
                ("MOTIVO_PAGAMENTO", "Motivo do pagamento", ""),
                ("DOCUMENTO_FINANCEIRO", "Documento financeiro", ""),
                ("DATA_DOCUMENTO_FINANCEIRO", "Data do documento financeiro", ""),
                ("CMT_PUBLICACAO", "Comandante / publicação", ""),
                ("CIDADE_UF", "Cidade / UF", "Belo Horizonte - MG"),
                ("DATA_ASSINATURA", "Data da assinatura", today),
                ("ASSINANTE_NOME", "Nome do assinante", ""),
                ("ASSINANTE_POSTO", "Posto/Graduação do assinante", ""),
                ("ASSINANTE_FUNCAO", "Função do assinante", "")
            ],
            GeneratedDocumentType.AdvanceChristmasBonus =>
            [
                ("CIDADE_UF", "Cidade / UF", "Belo Horizonte - MG"),
                ("DATA_REQUERIMENTO", "Data do requerimento", today),
                ("DATA_DESPACHO", "Data do despacho", today),
                ("ANO_REFERENCIA", "Ano de referência", DateTime.Today.Year.ToString()),
                ("PERIODO_ORDINAL", "Período de férias", "1º período"),
                ("DATA_INICIO_FERIAS", "Início das férias", ""),
                ("DATA_FIM_FERIAS", "Fim das férias", ""),
                ("BI_NUMERO", "Número do BI", ""),
                ("BI_DATA_EXTENSO", "Data do BI", ""),
                ("ORDENADOR", "Ordenador de Despesas — nome editável", ""),
                ("UNIDADE_SERVINDO", "Unidade em que serve", "4ª Cia PE"),
                ("DECLARACAO_REQUER", "Declaração", "É a primeira vez que requer.")
            ],
            GeneratedDocumentType.CoverSheet =>
            [
                ("PROTOCOLO_GERAL", "Protocolo geral", ""),
                ("SECAO", "Seção", "SSPP"),
                ("ANO_DOC", "Ano do processo", DateTime.Today.Year.ToString()),
                ("ASSUNTO_TEXTO", "Assunto", ""),
                ("PERIODO_CAPA", "Período", ""),
                ("ANEXOS_FOLHAS", "Quantidade de folhas", ""),
                ("NUM_PROCESSO", "Número do processo", "")
            ],
            GeneratedDocumentType.ExercisePreviousRequest =>
            [
                ("CIDADE_ESTADO", "Cidade / UF", "Belo Horizonte - MG"),
                ("DATA_REQUERIMENTO_EXTENSO", "Data do requerimento", todayLong),
                ("OBJETO", "Objeto", "Despesas de Exercícios Anteriores"),
                ("REFERENTE_A", "Referente a", ""),
                ("VALOR_REQUERIDO", "Valor requerido", ""),
                ("DOC_MATERIALIZOU", "Documento que materializou o direito", ""),
                ("BOLETIM_AVERBOU", "Boletim/anexo", ""),
                ("NUM_PROCESSO", "Número do processo/informação", ""),
                ("EB_REQUERIMENTO", "EB do requerimento", ""),
                ("EB_INFO", "EB da informação", ""),
                ("OM_NOME", "OM/lotação", "servindo na 4ª Cia PE"),
                ("SITUACAO", "Situação do militar", ""),
                ("BI_NUMERO", "Número do BI", ""),
                ("BI_DATA", "Data do BI", ""),
                ("CMT_COMPANHIA", "Comandante da Companhia", ""),
                ("OD_NOME_POSTO", "Ordenador de Despesas — nome/posto editável", ""),
                ("OD_FUNCAO", "Função do OD", "Ordenador de Despesas"),
                ("OD_EPOCA_NOME", "OD da época — nome", ""),
                ("OD_EPOCA_IDT", "OD da época — identidade", ""),
                ("OD_EPOCA_CPF", "OD da época — CPF", "")
            ],
            GeneratedDocumentType.JudicialPensionWorksheet =>
            [
                ("ALIMENTADO_NOME", "Nome do alimentado", ""),
                ("ALIMENTADO_CPF", "CPF do alimentado", ""),
                ("X_IMPLANTACAO", "Marcar implantação", "X"),
                ("X_MODIFICACAO", "Marcar modificação", ""),
                ("MES_ANO", "Mês/Ano de referência", ""),
                ("SOLDO", "Soldo", ""),
                ("AD_C_DISP_MIL", "Adicional Comp. Disp. Militar", ""),
                ("ADIC_HAB", "Adicional de Habilitação", ""),
                ("ADIC_MIL", "Adicional Militar", ""),
                ("AD_PERM", "Adicional Permanência", ""),
                ("HVOO", "HVOO", ""),
                ("GRAT_LOC_ESP", "Grat. Localidade Especial", ""),
                ("SALARIO_FAMILIA", "Salário-família", ""),
                ("SOMA_A", "Soma das receitas", ""),
                ("FUSEX_3", "FUSEx 3%", ""),
                ("DESC_DEP_FUSEX", "Desc. dependente FUSEx", ""),
                ("P_MIL_15", "Pensão militar 1,5%", ""),
                ("PENS_MIL_105", "Pensão militar 10,5%", ""),
                ("IR_Z10", "IR Z10", ""),
                ("PNR_106", "PNR 106", ""),
                ("SOLDO_DESP_MED", "10% soldo / despesa médica", ""),
                ("PJ_1", "Pensão judicial anterior 1", ""),
                ("PJ_2", "Pensão judicial anterior 2", ""),
                ("DESCONTOS_OBR", "Descontos obrigatórios", ""),
                ("SOMA_B", "Soma descontos obrigatórios", ""),
                ("VENCIMENTOS", "Vencimentos", ""),
                ("BASE_CALCULO", "Base de cálculo", ""),
                ("PERCENTUAL_PJ", "Percentual PJ", ""),
                ("VALOR_PENSAO", "Valor da pensão", ""),
                ("IR_REMUN", "IR remuneração", ""),
                ("IR_DESC_OBR", "IR descontos obrigatórios", ""),
                ("IR_DEP_QTD", "Qtd. dependentes IR", ""),
                ("IR_DEP", "Dedução dependentes IR", ""),
                ("IR_BASE", "Base IR", ""),
                ("IR_ALIQ", "Alíquota IR", ""),
                ("IR_TOTAL", "IR total", ""),
                ("ASS_NOME", "Assinante — nome", ""),
                ("ASS_NOME_GUERRA", "Assinante — nome de guerra", ""),
                ("ASS_POSTO_ABREV", "Assinante — P/G", "")
            ],
            GeneratedDocumentType.RemissiveIndex =>
            [
                ("ULTIMA_ATUALIZACAO", "Última atualização", today)
            ],
            GeneratedDocumentType.GratificationDiex =>
            [
                ("NATUREZA", "Natureza da atividade", ""),
                ("DESCRICAO", "Descrição do evento", ""),
                ("DOC_AUT", "Documento autorizador", ""),
                ("LOCAL", "Local", ""),
                ("PERIODO_INI", "Período inicial", ""),
                ("PERIODO_FIM", "Período final", ""),
                ("DIAS_EXT", "Duração em dias", ""),
                ("TOTAL_GERAL", "Valor total", ""),
                ("TOTAL_EXTENSO", "Valor total por extenso", ""),
                ("CONTATO", "Contato", "")
            ],
            GeneratedDocumentType.GratificationMap =>
            [
                ("NATUREZA", "Natureza do evento", ""),
                ("DESCRICAO", "Descrição do evento", ""),
                ("ENQUADRAMENTO", "Enquadramento", "Art. 5º do Decreto nº 11.002/2022"),
                ("LOCAL", "Local", ""),
                ("PERIODO_INI", "Período inicial", ""),
                ("PERIODO_FIM", "Período final", ""),
                ("DIAS_EXT", "Duração em dias", ""),
                ("DOC_AUT", "Documento autorizador", ""),
                ("BI", "BI da OM", ""),
                ("DATA_LOCAL", "Data/local", todayLong),
                ("TOTAL_GERAL", "Valor total", ""),
                ("TOTAL_EXTENSO", "Valor total por extenso", ""),
                ("RESUMO_EFETIVO", "Resumo do efetivo", "")
            ],
            _ => []
        };
    }

    public async Task<Dictionary<string, string>> GetTransportDefaultsAsync(
        MilitaryRecord military,
        IReadOnlyDictionary<string, string>? currentFields,
        bool useAddressFromDatabase,
        bool useBusesFromDatabase,
        CancellationToken cancellationToken = default)
    {
        const string addressLine1 = "_______________________________________________________________";
        const string addressLine2 = "______________________________________________________________________________________________";
        const string routeLine1 = "_______________________________________________________________";
        const string routeLine2 = "______________________________________________________________________________________________";
        const string meansLine = "________________________________________";
        const string busLine = "__________";
        const string fareLine = "__________";
        const string dailyLine = "_____________";
        const string monthlyLine = "____________";
        const string netLine = "____________";

        var summary = await _repository.GetTransportSummaryAsync(military, cancellationToken);
        var route = await _repository.GetTransportRouteDetailsAsync(military.Id, cancellationToken);
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        static string Money(double value, CultureInfo culture) => value > 0 ? value.ToString("N2", culture) : string.Empty;

        var days = summary.WorkingDays > 0 ? summary.WorkingDays : 22;
        if (currentFields is not null
            && currentFields.TryGetValue("DIAS_UTEIS", out var rawDays)
            && int.TryParse(new string((rawDays ?? string.Empty).Where(char.IsDigit).ToArray()), out var requestedDays)
            && requestedDays > 0)
            days = requestedDays;

        var buses = route.Buses.ToList();
        if (buses.Count == 0)
            buses = summary.Fares.Select((fare, index) => new BusRouteRecord
            {
                Index = index,
                Number = string.Empty,
                Description = string.Empty,
                Fare = fare.Fare
            }).ToList();
        else
        {
            for (var index = 0; index < buses.Count && index < summary.Fares.Count; index++)
                if (buses[index].Fare <= 0) buses[index].Fare = summary.Fares[index].Fare;
        }

        var fareValues = buses.Select(x => Math.Max(0, x.Fare)).Where(x => x > 0).ToList();
        if (fareValues.Count == 0)
            fareValues = summary.Fares.Select(x => Math.Max(0, x.Fare)).Where(x => x > 0).ToList();
        var totalDaily = fareValues.Sum() * 2.0;
        var totalMonthly = totalDaily * days;
        var sixPercent = Math.Max(0, summary.Salary) * 0.06;
        var sixPerDay = sixPercent / 30.0;
        var share = sixPerDay * days;
        var net = totalMonthly > 0
            ? (summary.BlockedByAttachedStatus ? 0 : Math.Max(0, totalMonthly - share))
            : ParseMoney(military.TransportAidValue);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DIAS_UTEIS"] = days.ToString(pt),
            ["SOLDO"] = string.Empty,
            ["SEIS_PORCENTO"] = string.Empty,
            ["SEIS_30"] = string.Empty,
            ["COTA_PARTE"] = string.Empty,
            ["ENDERECO1"] = addressLine1,
            ["ENDERECO2"] = addressLine2,
            ["PERCURSO1"] = routeLine1,
            ["PERCURSO2"] = routeLine2,
            ["MEIOS"] = meansLine,
            ["ONIBUS1_NR"] = busLine,
            ["ONIBUS1_TARIFA"] = fareLine,
            ["ONIBUS2_NR"] = busLine,
            ["ONIBUS2_TARIFA"] = fareLine,
            ["ONIBUS3_NR"] = busLine,
            ["ONIBUS3_TARIFA"] = fareLine,
            ["TOTAL_DIARIO"] = dailyLine,
            ["TOTAL_MENSAL"] = monthlyLine,
            ["LIQUIDO"] = netLine
        };

        if (useAddressFromDatabase)
        {
            var address = string.IsNullOrWhiteSpace(route.Destination) ? military.Address : route.Destination;
            if (!string.IsNullOrWhiteSpace(address))
            {
                var lines = SplitIntoLines(address.ToUpper(pt), 92, 2);
                values["ENDERECO1"] = lines.ElementAtOrDefault(0) ?? string.Empty;
                values["ENDERECO2"] = lines.ElementAtOrDefault(1) ?? string.Empty;
            }
        }

        if (useBusesFromDatabase)
        {
            values["SOLDO"] = Money(summary.Salary, pt);
            values["SEIS_PORCENTO"] = Money(sixPercent, pt);
            values["SEIS_30"] = Money(sixPerDay, pt);
            values["COTA_PARTE"] = Money(share, pt);
            values["MEIOS"] = "ÔNIBUS";
            values["PERCURSO1"] = "EM ANEXO";
            values["PERCURSO2"] = string.Empty;
            foreach (var pair in buses.Take(3).Select((bus, index) => (bus, index)))
            {
                var number = (pair.bus.Number ?? string.Empty).Trim();
                var description = (pair.bus.Description ?? string.Empty).Trim();
                var identification = !string.IsNullOrWhiteSpace(number) && !string.IsNullOrWhiteSpace(description)
                    ? $"{number} — {description}"
                    : !string.IsNullOrWhiteSpace(number) ? number
                    : !string.IsNullOrWhiteSpace(description) ? description
                    : busLine;
                identification = identification.ToUpper(pt);
                values[$"ONIBUS{pair.index + 1}_NR"] = identification;
                values[$"ONIBUS{pair.index + 1}_IDENTIFICACAO"] = identification;
                values[$"ONIBUS{pair.index + 1}_NOME"] = description.ToUpper(pt);
                values[$"LINHA{pair.index + 1}"] = number.ToUpper(pt);
                values[$"LINHA{pair.index + 1}_NOME"] = description.ToUpper(pt);
                values[$"ONIBUS{pair.index + 1}_TARIFA"] = pair.bus.Fare > 0 ? Money(pair.bus.Fare, pt) : fareLine;
            }
            values["TOTAL_DIARIO"] = totalDaily > 0 ? Money(totalDaily, pt) : dailyLine;
            values["TOTAL_MENSAL"] = totalMonthly > 0 ? Money(totalMonthly, pt) : monthlyLine;
            values["LIQUIDO"] = net > 0 ? Money(net, pt) : netLine;
        }

        return values;
    }

    private static IReadOnlyList<string> SplitIntoLines(string value, int maxLength, int maxLines)
    {
        var words = (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length == 0) current.Append(word);
            else if (current.Length + 1 + word.Length <= maxLength) current.Append(' ').Append(word);
            else
            {
                lines.Add(current.ToString());
                current.Clear();
                current.Append(word);
                if (lines.Count == maxLines - 1) break;
            }
        }
        if (current.Length > 0 && lines.Count < maxLines) lines.Add(current.ToString());
        while (lines.Count < maxLines) lines.Add(string.Empty);
        return lines.Take(maxLines).ToList();
    }

    private static double ParseMoney(string? value)
    {
        var text = (value ?? string.Empty).Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (double.TryParse(text, NumberStyles.Currency, CultureInfo.GetCultureInfo("pt-BR"), out var pt)) return Math.Max(0, pt);
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariant)) return Math.Max(0, invariant);
        return 0;
    }

    private async Task<string> GenerateOneAsync(
        GeneratedDocumentType type,
        MilitaryRecord military,
        Dictionary<string, string> mapping,
        string output,
        string? template,
        CancellationToken cancellationToken)
    {
        var baseName = SafeFileName($"{TypeSlug(type)}_{military.ShortRank}_{military.Name}_{DateTime.Today:yyyyMMdd}");
        if (!string.IsNullOrWhiteSpace(template) && File.Exists(template))
        {
            var extension = Path.GetExtension(template).ToLowerInvariant();
            var destination = UniquePath(Path.Combine(output, baseName + extension));
            if (extension is ".docx" or ".odt")
            {
                await RenderOfficeArchiveAsync(template, destination, mapping, cancellationToken);
                return destination;
            }
            File.Copy(template, destination, overwrite: false);
            return destination;
        }

        var fallback = UniquePath(Path.Combine(output, baseName + ".rtf"));
        await File.WriteAllTextAsync(fallback, BuildFallbackRtf(type, military, mapping), Encoding.ASCII, cancellationToken);
        return fallback;
    }

    private static async Task RenderOfficeArchiveAsync(
        string template,
        string destination,
        IReadOnlyDictionary<string, string> mapping,
        CancellationToken cancellationToken)
    {
        File.Copy(template, destination, overwrite: false);
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Update);
        foreach (var entry in archive.Entries.Where(IsTextEntry).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, 1024, false))
                text = await reader.ReadToEndAsync(cancellationToken);
            if (entry.FullName.Equals("content.xml", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(template).Equals(".odt", StringComparison.OrdinalIgnoreCase))
            {
                text = EnsureOdtWarNameBoldStyle(text);
                text = ReplaceOdtWarNameBold(text, mapping);
            }
            var replaced = ReplacePlaceholders(text, mapping);
            if (ReferenceEquals(replaced, text) || replaced == text) continue;
            using var stream = entry.Open();
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, false);
            await writer.WriteAsync(replaced.AsMemory(), cancellationToken);
        }
    }

    private static string EnsureOdtWarNameBoldStyle(string xml)
    {
        const string styleName = "SIGFURWarNameBold";
        if (xml.Contains($"style:name=\"{styleName}\"", StringComparison.Ordinal)) return xml;
        const string style = "<style:style style:name=\"SIGFURWarNameBold\" style:family=\"text\"><style:text-properties fo:font-weight=\"bold\" style:font-weight-asian=\"bold\" style:font-weight-complex=\"bold\" style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\"/></style:style>";
        var marker = "<office:automatic-styles>";
        var index = xml.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? xml : xml.Insert(index + marker.Length, style);
    }

    private static string ReplaceOdtWarNameBold(string xml, IReadOnlyDictionary<string, string> mapping)
    {
        if (!mapping.TryGetValue("NOME_GUERRA", out var warName) || string.IsNullOrWhiteSpace(warName)) return xml;
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        var before = mapping.GetValueOrDefault("NOME_ANTES", string.Empty).ToUpper(culture).Trim();
        var war = warName.ToUpper(culture).Trim();
        var after = mapping.GetValueOrDefault("NOME_DEPOIS", string.Empty).ToUpper(culture).Trim();
        // Em ODT, espaços nas bordas de um text node podem ser descartados pelo
        // editor. text:s garante que o nome não fique grudado no trecho em negrito.
        var beforeXml = string.IsNullOrWhiteSpace(before)
            ? string.Empty
            : (SecurityElement.Escape(before) ?? string.Empty) + "<text:s/>";
        var afterXml = string.IsNullOrWhiteSpace(after)
            ? string.Empty
            : "<text:s/>" + (SecurityElement.Escape(after) ?? string.Empty);
        var fragment = beforeXml
                       + "<text:span text:style-name=\"SIGFURWarNameBold\">"
                       + (SecurityElement.Escape(war) ?? string.Empty)
                       + "</text:span>"
                       + afterXml;
        foreach (var placeholder in new[]
        {
            "{{NOME_COM_GUERRA_BOLD}}", "[[NOME_COM_GUERRA_BOLD]]", "<<NOME_COM_GUERRA_BOLD>>", "$NOME_COM_GUERRA_BOLD$",
            "{{NOME_COM_GUERRA}}", "[[NOME_COM_GUERRA]]", "<<NOME_COM_GUERRA>>", "$NOME_COM_GUERRA$"
        })
        {
            xml = xml.Replace(placeholder, fragment, StringComparison.OrdinalIgnoreCase);
            var splitPattern = string.Join("(?:<[^>]+>)*", placeholder.Select(c => Regex.Escape(c.ToString())));
            try { xml = Regex.Replace(xml, splitPattern, _ => fragment, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch { }
        }
        return xml;
    }

    private static bool IsTextEntry(ZipArchiveEntry entry)
    {
        var name = entry.FullName.ToLowerInvariant();
        return name.EndsWith(".xml") || name.EndsWith(".rels") || name.EndsWith(".txt");
    }

    private static string ReplacePlaceholders(string source, IReadOnlyDictionary<string, string> mapping)
    {
        var text = source;
        foreach (var pair in mapping.OrderByDescending(x => x.Key.Length))
        {
            var key = pair.Key.Trim();
            var value = SecurityElement.Escape(pair.Value ?? string.Empty) ?? string.Empty;
            foreach (var placeholder in new[] { $"{{{{{key}}}}}", $"[[{key}]]", $"<<{key}>>", $"${key}$", $"{{{key}}}" })
            {
                text = text.Replace(placeholder, value, StringComparison.OrdinalIgnoreCase);
                var splitPattern = string.Join("(?:<[^>]+>)*", placeholder.Select(c => Regex.Escape(c.ToString())));
                try { text = Regex.Replace(text, splitPattern, _ => value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
                catch { }
            }
        }
        return text;
    }

    private static Dictionary<string, string> BuildMapping(GeneratedDocumentType type, MilitaryRecord m, IReadOnlyDictionary<string, string> fields)
    {
        var canonicalRank = MilitaryRankService.Canonicalize(string.IsNullOrWhiteSpace(m.Rank) ? m.ShortRank : m.Rank);
        var fullRank = canonicalRank.ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        var shortRank = MilitaryRankService.ShortName(canonicalRank);
        var documentRank = type == GeneratedDocumentType.TransportAid ? fullRank : shortRank;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ID"] = m.Id.ToString(CultureInfo.InvariantCulture),
            ["POSTO"] = documentRank,
            ["POSTO_ABREV"] = shortRank,
            ["POSTO_GRAD"] = documentRank,
            ["POSTO_EXTENSO"] = fullRank,
            ["NOME"] = (m.Name ?? string.Empty).ToUpperInvariant(),
            ["NOME_COMPLETO"] = (m.Name ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR")),
            ["NOME_GUERRA"] = (m.WarName ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR")),
            ["CPF"] = m.FormattedCpf,
            ["CPF_DIGITOS"] = MilitaryFormatting.Digits(m.Cpf),
            ["PREC_CP"] = m.PrecCp ?? string.Empty,
            ["PREC-CP"] = m.PrecCp ?? string.Empty,
            ["IDT"] = m.MilitaryId ?? string.Empty,
            ["BANCO"] = m.Bank ?? string.Empty,
            ["BANCO_DOCUMENTO"] = m.Bank ?? string.Empty,
            ["AGENCIA"] = m.Agency ?? string.Empty,
            ["AGÊNCIA"] = m.Agency ?? string.Empty,
            ["CONTA"] = m.Account ?? string.Empty,
            ["ENDERECO"] = m.Address ?? string.Empty,
            ["CEP"] = m.ZipCode ?? string.Empty,
            ["TELEFONE"] = m.Phone ?? string.Empty,
            ["EMAIL"] = m.Email ?? string.Empty,
            ["DATA_PRACA"] = m.EnlistmentDate ?? string.Empty,
            ["TEMPO_SERVICO"] = m.ServiceTimeText,
            ["VALOR_AT"] = m.TransportAidValue ?? string.Empty,
            ["SITUACAO_AT"] = m.TransportStatus,
            ["DATA_ATUAL"] = DateTime.Today.ToString("dd/MM/yyyy"),
            ["DATA_ATUAL_EXTENSO"] = DateTime.Today.ToString("dd 'de' MMMM 'de' yyyy", CultureInfo.GetCultureInfo("pt-BR")),
            ["OBSERVACOES_CADASTRO"] = m.Annotation ?? string.Empty
        };
        foreach (var pair in fields) map[pair.Key] = pair.Value ?? string.Empty;

        var highlighted = SIGFUR.Wpf.Controls.NameHighlightHelper.BuildSegments(m.Name, m.WarName);
        map["NOME_COM_GUERRA"] = string.Concat(highlighted.Select(x => x.Text)).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        map["NOME_COM_GUERRA_BOLD"] = map["NOME_COM_GUERRA"];
        map["NOME_GUERRA_DESTAQUE"] = (m.WarName ?? string.Empty).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));

        var nameParts = SplitNameForTemplate(m.Name, m.WarName);
        map["NOME_ANTES"] = nameParts.Before.ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        map["NOME_DEPOIS"] = nameParts.After.ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        map["NOME_ANTES_GUERRA"] = map["NOME_ANTES"];
        map["NOME_DEPOIS_GUERRA"] = map["NOME_DEPOIS"];
        map["MILITAR_NOME"] = map["NOME_COMPLETO"];
        map["MILITAR_PG_ABREV"] = shortRank;
        map["MILITAR_IDT"] = map["IDT"];
        map["MILITAR_PREC_CP"] = map["PREC_CP"];
        map["ALIMENTANTE_NOME"] = map["NOME_COMPLETO"];
        map["IDENTIDADE_EB_FMT"] = map["IDT"];
        map["CIDADE_ESTADO"] = map.GetValueOrDefault("CIDADE_ESTADO", map.GetValueOrDefault("CIDADE_UF", "Belo Horizonte - MG"));
        map["CIDADE_UF"] = map.GetValueOrDefault("CIDADE_UF", map["CIDADE_ESTADO"]);
        map["OM_NOME"] = map.GetValueOrDefault("OM_NOME", "4ª Companhia de Polícia do Exército");
        if (string.IsNullOrWhiteSpace(map.GetValueOrDefault("CMT_OM", string.Empty))) map["CMT_OM"] = "Cmt da " + map["OM_NOME"];
        if (string.IsNullOrWhiteSpace(map.GetValueOrDefault("REMETENTE_NOME", string.Empty))) map["REMETENTE_NOME"] = map["CMT_OM"];
        if (string.IsNullOrWhiteSpace(map.GetValueOrDefault("UNIDADE_SERVINDO", string.Empty))) map["UNIDADE_SERVINDO"] = map["OM_NOME"];
        if (string.IsNullOrWhiteSpace(map.GetValueOrDefault("CMD_NOME_POSTO", string.Empty))) map["CMD_NOME_POSTO"] = map["CMT_OM"];
        map["PREC"] = map["PREC_CP"];
        map["POSTO_ASSIN"] = documentRank;
        map["POSTO_ASSINATURA"] = documentRank;
        map["AGENCIA_NUM"] = map["AGENCIA"];
        map["AGENCIA_BANCO"] = map["AGENCIA"];
        map["DATA_HOJE"] = DateTime.Today.ToString("dd 'DE' MMMM 'DE' yyyy", CultureInfo.GetCultureInfo("pt-BR")).ToUpperInvariant();
        map["DATA_LOCAL"] = map["DATA_HOJE"];
        map["DATA_LOCAL_CMT"] = map["DATA_HOJE"];

        if (map.TryGetValue("MES", out var month) && !string.IsNullOrWhiteSpace(month)) map["MÊS"] = month.ToUpperInvariant();
        else if (map.TryGetValue("MÊS", out var accentedMonth)) map["MES"] = accentedMonth;
        if (type == GeneratedDocumentType.TransportAid)
        {
            var monthName = map.GetValueOrDefault("MES", map.GetValueOrDefault("MÊS", string.Empty));
            var yearText = map.GetValueOrDefault("ANO", DateTime.Today.Year.ToString(CultureInfo.InvariantCulture));
            var monthNumber = MonthNumberPt(monthName);
            map["PERIODO"] = monthNumber > 0 ? $"{monthNumber:00}/{yearText}" : yearText;
            map.Remove("BI_NUMERO");
            map.Remove("BI_DATA");
            map.Remove("BI_DATA_EXTENSO");
        }
        foreach (var dateKey in map.Keys.Where(key => key.Contains("DATA", StringComparison.OrdinalIgnoreCase)
                                                       && key.EndsWith("EXTENSO", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (!string.IsNullOrWhiteSpace(map[dateKey])) map[dateKey] = FormatDateLongPt(map[dateKey]);
        }
        if (map.TryGetValue("SEIS_30", out var sixThirty)) map["SEIS_PORCENTO_30"] = sixThirty;
        if (map.TryGetValue("BI_LICENCIAMENTO", out var licensingBi)) map["BI_LIC"] = licensingBi;
        else if (map.TryGetValue("BI_LIC", out var shortLicensingBi)) map["BI_LICENCIAMENTO"] = shortLicensingBi;
        if (map.TryGetValue("DATA_ASSINATURA", out var signatureDate)) map["DATA_ASSINATURA_EXTENSO"] = FormatDateLongPt(signatureDate);
        if (map.TryGetValue("DATA_REQUERIMENTO", out var requestDate)) map["DATA_REQUERIMENTO_EXTENSO"] = FormatDateLongPt(requestDate);
        if (map.TryGetValue("BAR_DATA", out var barDate)) map["BAR_DATA_EXTENSO"] = FormatDateLongPt(barDate);
        if (map.TryGetValue("BI_DATA_EXTENSO", out var biDate) && string.IsNullOrWhiteSpace(biDate)
            && map.TryGetValue("BI_DATA", out var rawBiDate)) map["BI_DATA_EXTENSO"] = FormatDateLongPt(rawBiDate);

        if (string.IsNullOrWhiteSpace(map.GetValueOrDefault("CMD_NOME_POSTO", string.Empty)))
        {
            var commander = string.Join(" - ", new[]
            {
                map.GetValueOrDefault("COMANDANTE_NOME", string.Empty),
                map.GetValueOrDefault("COMANDANTE_POSTO", string.Empty)
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
            map["CMD_NOME_POSTO"] = commander;
        }
        foreach (var key in new[] { "ORDENADOR", "ORDENADOR_NOME", "ORDENADOR_COMPLETO", "OD_NOME_POSTO" })
        {
            if (map.TryGetValue(key, out var maybeName) && IsObviouslyPlaceholderName(maybeName)) map[key] = string.Empty;
        }

        map["ORDENADOR_NOME"] = map.GetValueOrDefault("ORDENADOR", string.Empty);
        map["ORDENADOR_COMPLETO"] = string.Join(" - ", new[]
        {
            map.GetValueOrDefault("ORDENADOR", string.Empty),
            map.GetValueOrDefault("ORDENADOR_POSTO", string.Empty)
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(map.GetValueOrDefault("OD_NOME_POSTO", string.Empty)))
            map["OD_NOME_POSTO"] = map["ORDENADOR_COMPLETO"];

        map["ASS_NOME"] = map.GetValueOrDefault("ASSINANTE_NOME", string.Empty);
        map["ASS_NOME_GUERRA"] = map.GetValueOrDefault("ASSINANTE_NOME_GUERRA", string.Empty);
        map["ASS_POSTO_ABREV"] = map.GetValueOrDefault("ASSINANTE_POSTO", string.Empty);

        EnsureDefaultFinancialTemplateKeys(map);
        EnsureDefaultGratificationTemplateKeys(map);

        var destination = new PostalOmAddress
        {
            Street = map.GetValueOrDefault("LOGRADOURO_DESTINO", string.Empty),
            Number = map.GetValueOrDefault("NUMERO_DESTINO", string.Empty),
            Complement = map.GetValueOrDefault("COMPLEMENTO_DESTINO", string.Empty),
            Neighborhood = map.GetValueOrDefault("BAIRRO_DESTINO", string.Empty)
        };
        var composedDestination = PostalOmAddressService.BuildDestinationAddress(destination);
        if (!string.IsNullOrWhiteSpace(composedDestination)) map["ENDERECO_DESTINO"] = composedDestination;
        if (map.TryGetValue("CEP_DESTINO", out var destinationZip)) map["CEP_DESTINO"] = PostalOmAddressService.FormatZipCode(destinationZip);
        if (map.TryGetValue("ORIGEM_CEP", out var originZip)) map["ORIGEM_CEP"] = PostalOmAddressService.FormatZipCode(originZip);
        if (string.IsNullOrWhiteSpace(map.GetValueOrDefault("DESTINATARIO", string.Empty))
            && !string.IsNullOrWhiteSpace(map.GetValueOrDefault("OM_DESTINO", string.Empty)))
            map["DESTINATARIO"] = $"Sr Comandante do {map["OM_DESTINO"]}";

        var rawValue = map.GetValueOrDefault("VALOR", string.Empty);
        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            map["VALOR_EXTENSO"] = NumberToWordsService.Convert(rawValue, currency: true);
            if (decimal.TryParse(rawValue.Replace("R$", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Currency, CultureInfo.GetCultureInfo("pt-BR"), out var amount))
                map["VALOR"] = amount.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
        }
        else map["VALOR_EXTENSO"] = string.Empty;
        if (!map.ContainsKey("POSTO_DOCUMENTO")) map["POSTO_DOCUMENTO"] = documentRank;
        if (!map.ContainsKey("NOME_DOCUMENTO")) map["NOME_DOCUMENTO"] = map["NOME"];
        return map;
    }

    private static bool IsObviouslyPlaceholderName(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Normalize(NormalizationForm.FormD);
        normalized = new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return Regex.IsMatch(normalized, @"^(ORDENADOR\s+DE\s+DESPESAS\s*)?\d+$")
            || Regex.IsMatch(normalized, @"^DESPESAS\s*\d+$")
            || normalized.Equals("ORDENADOR DE DESPESAS 3", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureDefaultFinancialTemplateKeys(IDictionary<string, string> map)
    {
        foreach (var key in new[]
        {
            "AD_C_DISP_MIL", "ADIC_HAB", "ADIC_MIL", "AD_PERM", "HVOO", "GRAT_LOC_ESP", "SOMA_A",
            "FUSEX_3", "DESC_DEP_FUSEX", "P_MIL_15", "PENS_MIL_105", "IR_Z10", "PNR_106", "SOLDO_DESP_MED",
            "PJ_1", "PJ_2", "SOMA_B", "VENCIMENTOS", "DESCONTOS_OBRIGATORIOS", "VALOR_PENSAO",
            "X_IMPLANTACAO", "X_MODIFICACAO", "ALIMENTADO_NOME", "ALIMENTADO_CPF", "BASE_CALCULO", "DESCONTOS_OBR",
            "IR_ALIQ", "IR_BASE", "IR_DEP_QTD", "IR_DEP", "IR_DESC_OBR", "IR_REMUN", "IR_TOTAL", "MES_ANO", "PERCENTUAL_PJ",
            "SALARIO_FAMILIA", "ASS_NOME", "ASS_NOME_GUERRA", "ASS_POSTO_ABREV"
        })
        {
            if (!map.ContainsKey(key)) map[key] = string.Empty;
        }
    }

    private static void EnsureDefaultGratificationTemplateKeys(IDictionary<string, string> map)
    {
        var suffixes = new[] { "GENEX", "GENDIV", "GENBDA", "CEL", "TC", "MAJ", "CAP", "1TEN", "2TEN", "ASP", "ST", "1SGT", "2SGT", "3SGT", "CB", "SDEP", "SDEV" };
        foreach (var prefix in new[] { "EF", "DIAS", "UNIT", "SUB" })
            foreach (var suffix in suffixes)
                if (!map.ContainsKey($"{prefix}_{suffix}")) map[$"{prefix}_{suffix}"] = string.Empty;

        foreach (var key in new[] { "ROW_PG", "ROW_EF", "ROW_UNIT", "ROW_SUB", "TOTAL_GERAL", "TOTAL_EXTENSO", "DIAS_TOTAL", "RESUMO_EFETIVO" })
            if (!map.ContainsKey(key)) map[key] = string.Empty;
    }

    private static (string Before, string War, string After) SplitNameForTemplate(string? fullName, string? warName)
    {
        var full = (fullName ?? string.Empty).Trim().ToUpperInvariant();
        var war = (warName ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(full) || string.IsNullOrWhiteSpace(war)) return (full, war, string.Empty);
        var index = full.IndexOf(war, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0) return (full, war, string.Empty);
        return (full[..index].TrimEnd(), full.Substring(index, war.Length), full[(index + war.Length)..].TrimStart());
    }

    private static int MonthNumberPt(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (int.TryParse(new string(raw.Where(char.IsDigit).ToArray()), out var number) && number is >= 1 and <= 12) return number;
        var normalized = raw.Normalize(NormalizationForm.FormD);
        normalized = new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
        var months = new[] { "JANEIRO", "FEVEREIRO", "MARCO", "ABRIL", "MAIO", "JUNHO", "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO" };
        var index = Array.FindIndex(months, item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index + 1 : 0;
    }

    private static string FormatDateLongPt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy" };
        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var date)
            || DateTime.TryParse(value, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out date))
            return date.ToString("dd 'de' MMMM 'de' yyyy", CultureInfo.GetCultureInfo("pt-BR"));
        return value.Trim();
    }

    private static string BuildFallbackRtf(GeneratedDocumentType type, MilitaryRecord m, IReadOnlyDictionary<string, string> mapping)
    {
        static string Rtf(string value) => new string((value ?? string.Empty).SelectMany(ch => ch switch
        {
            '\\' => "\\\\",
            '{' => "\\{",
            '}' => "\\}",
            '\n' => "\\line ",
            '\r' => string.Empty,
            _ when ch > 127 => $"\\u{(short)ch}?",
            _ => ch.ToString()
        }).ToArray());

        var body = new StringBuilder();
        body.AppendLine(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}{\f1 Times New Roman;}}\viewkind4\uc1");
        body.AppendLine(@"\paperw11906\paperh16838\margl1417\margr1417\margt1134\margb1134");
        body.AppendLine($@"\qc\f0\fs30\b {Rtf(DisplayName(type).ToUpperInvariant())}\b0\par");
        body.AppendLine(@"\ql\fs22\par");
        var fallbackRank = type == GeneratedDocumentType.TransportAid
            ? MilitaryRankService.Canonicalize(string.IsNullOrWhiteSpace(m.Rank) ? m.ShortRank : m.Rank).ToUpper(CultureInfo.GetCultureInfo("pt-BR"))
            : m.ShortRank;
        body.AppendLine($@"\b Militar:\b0  {Rtf(fallbackRank + " " + m.Name)}\par");
        body.AppendLine($@"\b Nome de guerra:\b0  {Rtf(m.WarName)}\par");
        body.AppendLine($@"\b CPF:\b0  {Rtf(m.FormattedCpf)}\par");
        body.AppendLine($@"\b PREC-CP / IDT:\b0  {Rtf(m.PrecCp + " / " + m.MilitaryId)}\par");
        body.AppendLine($@"\b Endereço:\b0  {Rtf(m.Address)}\par\par");
        foreach (var pair in mapping.Where(x => !StandardKeys.Contains(x.Key) && !string.IsNullOrWhiteSpace(x.Value)).OrderBy(x => x.Key))
            body.AppendLine($@"\b {Rtf(pair.Key.Replace('_', ' '))}:\b0  {Rtf(pair.Value)}\par");
        body.AppendLine(@"\par\qc Documento gerado pelo SIGFUR.\par}");
        return body.ToString();
    }

    private static readonly HashSet<string> StandardKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ID", "POSTO", "POSTO_ABREV", "POSTO_GRAD", "POSTO_EXTENSO", "NOME", "NOME_COMPLETO", "NOME_GUERRA",
        "CPF", "CPF_DIGITOS", "PREC_CP", "PREC-CP", "IDT", "BANCO", "BANCO_DOCUMENTO", "AGENCIA", "AGÊNCIA",
        "CONTA", "ENDERECO", "CEP", "TELEFONE", "EMAIL", "DATA_PRACA", "TEMPO_SERVICO", "VALOR_AT", "SITUACAO_AT",
        "DATA_ATUAL", "DATA_ATUAL_EXTENSO", "OBSERVACOES_CADASTRO", "POSTO_DOCUMENTO", "NOME_DOCUMENTO",
        "NOME_COM_GUERRA", "NOME_GUERRA_DESTAQUE", "NOME_ANTES_GUERRA", "NOME_DEPOIS_GUERRA", "MILITAR_NOME", "MILITAR_PG_ABREV", "MILITAR_IDT", "MILITAR_PREC_CP", "ALIMENTANTE_NOME", "IDENTIDADE_EB_FMT"
    };

    private static IReadOnlyList<string> TemplateNames(GeneratedDocumentType type) => type switch
    {
        GeneratedDocumentType.TransportAid => ["SAT_-_MODELO 2025.odt", "SAT - MODELO 2025.odt", "AUXILIO_TRANSPORTE_TEMPLATE.docx", "AUXILIO_TRANSPORTE.odt"],
        GeneratedDocumentType.PecuniaryCompensation => ["PECUNIARIA_TEMPLATE.docx", "PECUNIARIA TEMPLACE.docx", "PECUNIARIA_template.odt", "PECUNIARIA.odt"],
        GeneratedDocumentType.AuthenticPaymentCopy => ["COPIA_AUTENTICA_PAGAMENTO_TEMPLATE.docx", "COPIA_AUTENTICA_PAGAMENTO_TEMPLATE(5).docx", "Copia Autêntica - (PAGAMENTO)(1).docx", "Copia Autêntica - (PAGAMENTO).docx"],
        GeneratedDocumentType.AdvanceChristmasBonus => ["REQ ANT ADC NATAL TEN DINALI.odt", "ADIANTAMENTO_NATALINO_TEMPLATE.docx"],
        GeneratedDocumentType.PostalLabel => ["CORREIOS.odt", "CORREIOS(2).odt", "CORREIOS_TEMPLATE.docx"],
        GeneratedDocumentType.CoverSheet => ["CAPA_TEMPLATE.docx", "CAPA_PHPM_TEMPLATE.docx", "capa PHPM_TEMPLATE.docx"],
        GeneratedDocumentType.ExercisePreviousRequest => ["REQUERIMENTO_TEMPLATE.docx", "REQUERIMENTO_EA_TEMPLATE.docx"],
        GeneratedDocumentType.JudicialPensionWorksheet => ["PENSAO TEMPLATE.docx", "PENSAO_TEMPLATE.docx", "FICHA_CONTROLE_PENSAO.docx"],
        GeneratedDocumentType.RemissiveIndex => ["INDICE_REMISSIVO_SIGFUR_TEMPLATE.odt", "INDICE_REMISSIVO_TEMPLATE.odt"],
        GeneratedDocumentType.GratificationDiex => ["Grat Rep Diex.docx", "GRAT_REP_DIEX.docx"],
        GeneratedDocumentType.GratificationMap => ["Mapa_Grat_Rep__4ciaPE._att.docx", "MAPA_GRAT_REP.docx"],
        _ => []
    };

    private static string NormalizeFileName(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray()).ToUpperInvariant();
    }

    private static string SafeFileName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) value = value.Replace(ch, '_');
        value = Regex.Replace(value, @"\s+", "_").Trim('_', '.');
        return value.Length > 140 ? value[..140] : value;
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var index = 2; index < 10000; index++)
        {
            var candidate = Path.Combine(dir, $"{name}_{index}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
    }
}
