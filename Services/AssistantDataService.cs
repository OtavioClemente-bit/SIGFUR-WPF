using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Ponte estritamente controlada entre o modelo e os dados locais do SIGFUR.
/// Todas as consultas são somente leitura; ações operacionais voltam como pendências
/// para confirmação do operador na interface.
/// </summary>
public sealed class AssistantDataService
{
    private readonly AppPaths _paths;
    private readonly MilitaryRepository _military;
    private readonly VacationPlanService _vacations;
    private readonly IntelligentBulletinService _bulletins;
    private readonly LegislationService _legislation;
    private readonly PaystubService _paystubs;
    private readonly LicensedTransferredRepository _licensedTransferred;
    private readonly ReminderService _reminders;
    private readonly DutyRosterService _dutyRoster;
    private readonly AbsenceService _absences;
    private readonly BulletinKnowledgeService _bulletinKnowledge;
    private readonly PaymentConferenceService _paymentConference;
    private readonly LogService _log;

    public AssistantDataService(
        AppPaths paths,
        MilitaryRepository military,
        VacationPlanService vacations,
        IntelligentBulletinService bulletins,
        LegislationService legislation,
        PaystubService paystubs,
        LicensedTransferredRepository licensedTransferred,
        ReminderService reminders,
        DutyRosterService dutyRoster,
        AbsenceService absences,
        BulletinKnowledgeService bulletinKnowledge,
        PaymentConferenceService paymentConference,
        LogService log)
    {
        _paths = paths;
        _military = military;
        _vacations = vacations;
        _bulletins = bulletins;
        _legislation = legislation;
        _paystubs = paystubs;
        _licensedTransferred = licensedTransferred;
        _reminders = reminders;
        _dutyRoster = dutyRoster;
        _absences = absences;
        _bulletinKnowledge = bulletinKnowledge;
        _paymentConference = paymentConference;
        _log = log;
    }

    public JsonArray BuildToolDefinitions()
    {
        static JsonObject Tool(string name, string description, JsonObject properties, string[] required)
            => new()
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["strict"] = true,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = new JsonArray(required.Select(x => (JsonNode?)x).ToArray()),
                        ["additionalProperties"] = false
                    }
                }
            };

        static JsonObject StringProperty(string description) => new() { ["type"] = "string", ["description"] = description };
        static JsonObject IntegerProperty(string description, int minimum, int maximum) => new() { ["type"] = "integer", ["description"] = description, ["minimum"] = minimum, ["maximum"] = maximum };
        static JsonObject BooleanProperty(string description) => new() { ["type"] = "boolean", ["description"] = description };
        static JsonObject SensitiveFieldsProperty(string description, params string[] allowed)
            => new()
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(allowed.Select(x => (JsonNode?)x).ToArray())
                },
                ["maxItems"] = allowed.Length
            };

        return
        [
            Tool("buscar_militar",
                "Localiza militares ativos no banco oficial do SIGFUR pelo nome completo, nome de guerra, posto/graduação, CPF, identidade ou PREC-CP. Quando o operador pedir explicitamente CPF, PREC-CP ou identidade, informe somente os campos solicitados em campos_solicitados. Use uma lista vazia quando nenhum dado pessoal tiver sido pedido.",
                new JsonObject
                {
                    ["consulta"] = StringProperty("Nome, nome de guerra, posto/graduação ou identificador informado pelo operador."),
                    ["limite"] = IntegerProperty("Quantidade máxima de resultados.", 1, 20),
                    ["campos_solicitados"] = SensitiveFieldsProperty("Dados pessoais pedidos explicitamente pelo operador. Use [] quando nenhum tiver sido solicitado.", "cpf", "prec_cp", "identidade")
                }, ["consulta", "limite", "campos_solicitados"]),

            Tool("consultar_resumo_militar",
                "Obtém resumo cadastral e funcional de um militar localizado no SIGFUR. Dados pessoais permanecem ocultos por padrão; quando o operador pedir explicitamente um deles, liste somente os campos pedidos em campos_solicitados.",
                new JsonObject
                {
                    ["militar"] = StringProperty("Nome, nome de guerra ou ID do militar."),
                    ["campos_solicitados"] = SensitiveFieldsProperty("Dados pessoais pedidos explicitamente pelo operador. Use [] quando nenhum tiver sido solicitado.", "cpf", "prec_cp", "identidade", "data_nascimento", "telefone", "email", "endereco")
                }, ["militar", "campos_solicitados"]),

            Tool("consultar_ferias",
                "Consulta períodos de férias cadastrados para um militar e ano, incluindo dias e situação do pagamento do adicional quando registrado.",
                new JsonObject
                {
                    ["militar"] = StringProperty("Nome, nome de guerra ou ID do militar."),
                    ["ano"] = IntegerProperty("Ano do plano de férias.", 2000, 2100)
                }, ["militar", "ano"]),

            Tool("consultar_alertas_corrida_pagamento",
                "Consulta férias que precisam entrar na corrida de pagamento. Regra operacional: férias que iniciam em um mês devem ser pedidas no pagamento do mês anterior; a 1ª corrida sugerida é dia 15 do mês anterior à competência do pagamento e a 2ª corrida é o 3º dia útil da competência. Use sempre que houver pergunta sobre férias, pagamento pendente, corrida de pagamento, quem ficou sem receber ou lembrete operacional.",
                new JsonObject
                {
                    ["ano_pagamento"] = IntegerProperty("Ano da competência do pagamento. Use 0 para ciclos atuais/próximos.", 0, 2100),
                    ["mes_pagamento"] = IntegerProperty("Mês da competência do pagamento. Use 0 para ciclos atuais/próximos.", 0, 12),
                    ["limite"] = IntegerProperty("Quantidade máxima de pendências detalhadas.", 1, 80),
                    ["incluir_pagos"] = BooleanProperty("Incluir também militares já marcados como pagos no Plano de Férias.")
                }, ["ano_pagamento", "mes_pagamento", "limite", "incluir_pagos"]),

            Tool("consultar_efetivo",
                "Conta o efetivo cadastrado no banco oficial do SIGFUR, com total geral, total filtrado, distribuição por posto/graduação e indicadores administrativos. Use quando o operador perguntar quantos militares há no sistema.",
                new JsonObject
                {
                    ["filtro"] = StringProperty("Filtro opcional por posto, nome, formação ou situação. Use string vazia para contar todos."),
                    ["incluir_licenciados_transferidos"] = BooleanProperty("Somar também registros do módulo Licenciados/Transferidos.")
                }, ["filtro", "incluir_licenciados_transferidos"]),

            Tool("consulta_operacional_militar",
                "Consulta operacional completa de um militar, cruzando cadastro, férias, auxílio-transporte, documentos, contracheques, lembretes, faltas/atrasos, escala e boletins/aditamentos indexados. Use para perguntas sobre uma pessoa específica.",
                new JsonObject
                {
                    ["militar"] = StringProperty("Nome, nome de guerra, CPF, PREC-CP ou ID do militar."),
                    ["ano"] = IntegerProperty("Ano principal para férias e consultas anuais.", 2000, 2100),
                    ["mes"] = IntegerProperty("Mês principal para contracheques, faltas e escala.", 1, 12),
                    ["assuntos"] = StringProperty("Assuntos de boletim separados por vírgula, por exemplo férias, adicional de férias, auxílio alimentação. Use string vazia para busca geral da pessoa."),
                    ["limite_boletins"] = IntegerProperty("Quantidade máxima de menções em boletins/aditamentos.", 1, 50)
                }, ["militar", "ano", "mes", "assuntos", "limite_boletins"]),

            Tool("consultar_auxilio_transporte",
                "Consulta o auxílio-transporte do militar, incluindo linhas de ônibus, números, nomes ou trajetos, categorias, tarifas, dias úteis e memória de cálculo.",
                new JsonObject
                {
                    ["militar"] = StringProperty("Nome, nome de guerra ou ID do militar.")
                }, ["militar"]),

            Tool("pesquisar_boletins",
                "Pesquisa menções já indexadas nos boletins do SIGFUR e retorna BI, data, página, categoria e contexto. Pode pesquisar por militar, assunto ou ambos.",
                new JsonObject
                {
                    ["militar"] = StringProperty("Nome ou nome de guerra. Use string vazia quando a pesquisa for apenas por assunto."),
                    ["assunto"] = StringProperty("Termo do assunto, por exemplo férias, saque, apresentação ou pagamento. Pode ser vazio."),
                    ["limite"] = IntegerProperty("Quantidade máxima de achados.", 1, 30)
                }, ["militar", "assunto", "limite"]),

            Tool("consultar_regras_boletim",
                "Consulta a base oficial local de regras e exemplos de boletins/SIPPES aprendida a partir dos lembretes do gerador de direito e de aditamentos reais. Use antes de criar, revisar ou orientar qualquer publicação de pagamento.",
                new JsonObject
                {
                    ["consulta"] = StringProperty("Tipo de direito, ação ou título do boletim, por exemplo auxílio-transporte despesa a anular, adicional de habilitação ou férias atrasadas."),
                    ["limite"] = IntegerProperty("Quantidade máxima de regras relacionadas.", 1, 10)
                }, ["consulta", "limite"]),


            Tool("gerar_corpo_boletim",
                "Monta um corpo de boletim/SIPPES pronto para revisão, usando regras locais, modelos do SIGFUR e a biblioteca offline de legislação. Use quando o operador pedir para criar, escrever, montar ou melhorar corpo de boletim, nota para BI, aditamento ou texto de publicação.",
                new JsonObject
                {
                    ["assunto"] = StringProperty("Assunto principal do boletim, por exemplo férias, adicional de férias, auxílio-transporte, pensão judicial, gratificação de representação."),
                    ["acao"] = StringProperty("Ação desejada, por exemplo ordem de saque, implantação, saque de atrasados, despesa a anular, concessão, atualização. Use string vazia se não informado."),
                    ["militar"] = StringProperty("Nome, nome de guerra, CPF, PREC-CP ou ID do militar. Use string vazia para modelo genérico."),
                    ["ano"] = IntegerProperty("Ano de referência ou do plano. Use 0 se não informado.", 0, 2100),
                    ["periodo"] = StringProperty("Período ou datas informadas pelo operador. Use string vazia se não informado."),
                    ["dados_adicionais"] = StringProperty("Dados complementares do pedido em texto livre, como dias, BI, valor, decisão, competência, motivo ou observações. Use string vazia se não informado."),
                    ["usar_legislacao_local"] = BooleanProperty("Pesquisar e citar referências da biblioteca local de legislação quando disponível."),
                    ["limite_fontes"] = IntegerProperty("Quantidade máxima de fontes locais a retornar.", 0, 10)
                }, ["assunto", "acao", "militar", "ano", "periodo", "dados_adicionais", "usar_legislacao_local", "limite_fontes"]),


            Tool("pesquisar_todos_boletins",
                "Pesquisa de forma ampliada em Boletim Inteligente, Aditamento do Furriel e boletins externos indexados. Usa texto estruturado e texto integral quando disponível. Use quando o operador perguntar se há publicação, BI ou dado de uma pessoa.",
                new JsonObject
                {
                    ["militar"] = StringProperty("Nome, nome de guerra, CPF, PREC-CP ou ID do militar. Use string vazia quando for busca apenas por assunto."),
                    ["assunto"] = StringProperty("Assunto desejado. Use string vazia para buscar qualquer menção da pessoa."),
                    ["limite"] = IntegerProperty("Quantidade máxima de resultados.", 1, 80)
                }, ["militar", "assunto", "limite"]),

            Tool("consultar_documentos_militar",
                "Lista metadados dos documentos cadastrados na carteira do militar, sem enviar o conteúdo integral automaticamente.",
                new JsonObject { ["militar"] = StringProperty("Nome, nome de guerra ou ID do militar.") }, ["militar"]),

            Tool("pesquisar_legislacao_local",
                "Pesquisa a biblioteca offline de legislação indexada no SIGFUR. Retorna referências, páginas e trechos; não invente norma quando não houver resultado.",
                new JsonObject
                {
                    ["consulta"] = StringProperty("Pergunta ou palavras-chave jurídicas/administrativas."),
                    ["limite"] = IntegerProperty("Quantidade máxima de trechos.", 1, 20)
                }, ["consulta", "limite"]),

            Tool("consultar_contracheques",
                "Lista os contracheques PDF já salvos localmente para um militar, com competência, arquivo, data e tamanho. Não abre nem envia o conteúdo integral automaticamente.",
                new JsonObject
                {
                    ["militar"] = StringProperty("Nome, nome de guerra ou ID do militar."),
                    ["limite"] = IntegerProperty("Quantidade máxima de registros.", 1, 24)
                }, ["militar", "limite"]),

            Tool("buscar_licenciados_transferidos",
                "Pesquisa militares licenciados ou transferidos já registrados no SIGFUR pelo nome, nome de guerra, destino ou motivo. Quando o operador pedir explicitamente CPF ou PREC-CP, informe somente os campos solicitados.",
                new JsonObject
                {
                    ["consulta"] = StringProperty("Nome, nome de guerra, destino ou motivo."),
                    ["limite"] = IntegerProperty("Quantidade máxima de resultados.", 1, 20),
                    ["campos_solicitados"] = SensitiveFieldsProperty("Dados pessoais pedidos explicitamente pelo operador. Use [] quando nenhum tiver sido solicitado.", "cpf", "prec_cp")
                }, ["consulta", "limite", "campos_solicitados"]),

            Tool("consultar_lembretes",
                "Consulta lembretes operacionais do SIGFUR, incluindo vencimento, prioridade e situação.",
                new JsonObject
                {
                    ["consulta"] = StringProperty("Termo para filtrar título ou conteúdo. Use string vazia para listar os mais relevantes."),
                    ["incluir_concluidos"] = BooleanProperty("Incluir lembretes já concluídos."),
                    ["limite"] = IntegerProperty("Quantidade máxima de resultados.", 1, 30)
                }, ["consulta", "incluir_concluidos", "limite"]),

            Tool("consultar_faltas_atrasos",
                "Consulta faltas e atrasos cadastrados de um militar em determinado mês, com justificativa e medidas registradas.",
                new JsonObject
                {
                    ["militar"] = StringProperty("Nome, nome de guerra ou ID do militar."),
                    ["ano"] = IntegerProperty("Ano da consulta.", 2000, 2100),
                    ["mes"] = IntegerProperty("Mês da consulta.", 1, 12)
                }, ["militar", "ano", "mes"]),

            Tool("consultar_escala_servico",
                "Consulta os dias de serviço e marcações do SGT de Dia para um militar em determinado mês.",
                new JsonObject
                {
                    ["militar"] = StringProperty("Nome, nome de guerra ou ID do militar."),
                    ["ano"] = IntegerProperty("Ano da consulta.", 2000, 2100),
                    ["mes"] = IntegerProperty("Mês da consulta.", 1, 12)
                }, ["militar", "ano", "mes"]),

            Tool("conferir_pagamento_ia",
                "Executa uma conferência de pagamento usando os aditamentos do Furriel indexados e os contracheques salvos. Retorna resumo de OK, não recebeu, valor divergente, sem contracheque e pendências. Use para verificar quem não recebeu algum direito.",
                new JsonObject
                {
                    ["ano"] = IntegerProperty("Ano do pagamento. Use 0 para usar a configuração salva da Conferência de Pagamento.", 0, 2100),
                    ["mes"] = IntegerProperty("Mês do pagamento. Use 0 para usar a configuração salva da Conferência de Pagamento.", 0, 12),
                    ["limite_linhas"] = IntegerProperty("Quantidade máxima de linhas detalhadas na resposta da ferramenta.", 1, 200),
                    ["salvar_relatorio"] = BooleanProperty("Salvar CSV da conferência na pasta oficial de relatórios.")
                }, ["ano", "mes", "limite_linhas", "salvar_relatorio"]),

            Tool("criar_lembrete_operacional",
                "Cria um lembrete operacional no SIGFUR quando o operador pedir para lembrar, cobrar ou não esquecer uma providência.",
                new JsonObject
                {
                    ["titulo"] = StringProperty("Título curto do lembrete."),
                    ["data"] = StringProperty("Data no formato dd/mm/aaaa. Use string vazia quando não houver data."),
                    ["prioridade"] = StringProperty("Prioridade: Normal, Urgente, Urgentíssimo ou Baixa."),
                    ["descricao"] = StringProperty("Descrição objetiva do que deve ser feito.")
                }, ["titulo", "data", "prioridade", "descricao"]),

            Tool("localizar_arquivos_gerados",
                "Localiza arquivos já gerados pelo SIGFUR para abrir ou preparar impressão. Pesquisa apenas dentro da pasta oficial de documentos gerados.",
                new JsonObject
                {
                    ["consulta"] = StringProperty("Parte do nome do arquivo, militar, tipo do documento ou extensão."),
                    ["limite"] = IntegerProperty("Quantidade máxima de arquivos.", 1, 30)
                }, ["consulta", "limite"]),

            Tool("solicitar_impressao",
                "Prepara uma ação de impressão para confirmação do operador. Nunca imprime silenciosamente; abre a fila de impressão do SIGFUR após autorização.",
                new JsonObject
                {
                    ["arquivo_ou_consulta"] = StringProperty("Nome retornado por localizar_arquivos_gerados ou parte do nome do arquivo."),
                    ["copias"] = IntegerProperty("Número de cópias.", 1, 20)
                }, ["arquivo_ou_consulta", "copias"])
        ];
    }

    public async Task<AssistantToolExecutionResult> ExecuteAsync(string name, JsonObject arguments, AssistantSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var result = name switch
            {
                "buscar_militar" => await SearchMilitaryAsync(arguments, settings, cancellationToken),
                "consultar_resumo_militar" => await GetMilitarySummaryAsync(arguments, settings, cancellationToken),
                "consultar_ferias" => await GetVacationsAsync(arguments, settings, cancellationToken),
                "consultar_alertas_corrida_pagamento" => await GetPaymentRunVacationAlertsAsync(arguments, cancellationToken),
                "consultar_efetivo" => await CountPersonnelAsync(arguments, cancellationToken),
                "consulta_operacional_militar" => await GetOperationalMilitaryContextAsync(arguments, settings, cancellationToken),
                "consultar_auxilio_transporte" => await GetTransportAidAsync(arguments, cancellationToken),
                "pesquisar_boletins" => await SearchBulletinsAsync(arguments, settings, cancellationToken),
                "pesquisar_todos_boletins" => await SearchAllBulletinsToolAsync(arguments, settings, cancellationToken),
                "consultar_regras_boletim" => await SearchBulletinRulesAsync(arguments, cancellationToken),
                "gerar_corpo_boletim" => await GenerateBulletinBodyAsync(arguments, settings, cancellationToken),
                "consultar_documentos_militar" => await GetMilitaryDocumentsAsync(arguments, settings, cancellationToken),
                "pesquisar_legislacao_local" => await SearchLegislationAsync(arguments, settings, cancellationToken),
                "consultar_contracheques" => await GetPaystubsAsync(arguments, cancellationToken),
                "buscar_licenciados_transferidos" => await SearchLicensedTransferredAsync(arguments, settings, cancellationToken),
                "consultar_lembretes" => await GetRemindersAsync(arguments, cancellationToken),
                "consultar_faltas_atrasos" => await GetAbsencesAsync(arguments, cancellationToken),
                "consultar_escala_servico" => await GetDutyRosterAsync(arguments, cancellationToken),
                "conferir_pagamento_ia" => await RunPaymentConferenceForAssistantAsync(arguments, cancellationToken),
                "criar_lembrete_operacional" => await CreateOperationalReminderAsync(arguments, cancellationToken),
                "localizar_arquivos_gerados" => await FindGeneratedFilesAsync(arguments, cancellationToken),
                "solicitar_impressao" => await RequestPrintAsync(arguments, cancellationToken),
                _ => new AssistantToolExecutionResult { OutputJson = JsonSerializer.Serialize(new { success = false, error = $"Ferramenta desconhecida: {name}" }) }
            };
            var explicitSensitiveAccess = name is "consulta_operacional_militar" or "consultar_efetivo" or "pesquisar_todos_boletins" or "conferir_pagamento_ia" or "criar_lembrete_operacional" or "consultar_alertas_corrida_pagamento" or "gerar_corpo_boletim"
                                          || ((name is "buscar_militar" or "consultar_resumo_militar" or "buscar_licenciados_transferidos")
                                          && HasRequestedSensitiveFields(arguments));
            if (settings.RedactSensitiveData && !explicitSensitiveAccess)
                result.OutputJson = AssistantAttachmentService.RedactSensitiveData(result.OutputJson);
            return result;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"Falha na ferramenta do Assistente SIGFUR: {name}", ex);
            return new AssistantToolExecutionResult
            {
                OutputJson = JsonSerializer.Serialize(new { success = false, error = ex.Message }),
                Summary = $"{name}: falha"
            };
        }
    }

    private async Task<AssistantToolExecutionResult> SearchBulletinRulesAsync(JsonObject args, CancellationToken ct)
    {
        var query = GetString(args, "consulta");
        var limit = GetInt(args, "limite", 5, 1, 10);
        var rules = await _bulletinKnowledge.SearchAsync(query, limit, ct);
        var result = rules.Select(rule => new
        {
            id = rule.Id,
            titulo = rule.Title,
            categoria = rule.Category,
            acao = rule.Action,
            resumo = rule.Summary,
            campos_obrigatorios = rule.RequiredFields.Select(field => new { chave = field.Key, campo = field.Label, origem = field.Source, descricao = field.Description }),
            texto_obrigatorio = rule.RequiredText.Select(item => new { item.Label, alternativas = item.AnyOf }),
            checklist = rule.Checklist,
            publicacoes_relacionadas = rule.RelatedPublications,
            orientacao_ia = rule.AiGuidance,
            padrao_exemplo = rule.ExamplePattern,
            fontes = rule.SourceReferences
        }).ToList();
        return Result(new { success = true, consulta = query, quantidade = result.Count, regras = result }, $"Regras de boletim: {result.Count} encontrada(s)");
    }

    private async Task<AssistantToolExecutionResult> GenerateBulletinBodyAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var subject = GetString(args, "assunto");
        var action = GetString(args, "acao");
        var militaryReference = GetString(args, "militar");
        var year = GetInt(args, "ano", 0, 0, 2100);
        var period = GetString(args, "periodo");
        var extra = GetString(args, "dados_adicionais");
        var useLegislation = GetBool(args, "usar_legislacao_local", true);
        var sourceLimit = GetInt(args, "limite_fontes", 5, 0, 10);
        var query = string.Join(' ', new[] { subject, action, period, extra }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(query)) query = "boletim administrativo pagamento";

        var military = string.IsNullOrWhiteSpace(militaryReference) ? null : await ResolveMilitaryAsync(militaryReference, ct);
        var rules = await _bulletinKnowledge.SearchAsync(query, 6, ct);
        var legislationHits = useLegislation && sourceLimit > 0 ? await _legislation.SearchAsync(BuildLegislationQuery(subject, action, query), sourceLimit) : [];

        var body = BuildOperationalBulletinBody(subject, action, military, year, period, extra, rules, legislationHits);
        var fields = BuildBulletinMissingFields(subject, action, military, year, period, extra);
        var sources = legislationHits.Select(hit => new { referencia = hit.Reference, titulo = hit.Title, pagina = hit.Page, trecho = TrimText(hit.Snippet, 900) }).ToList();
        var ruleRows = rules.Select(rule => new
        {
            titulo = rule.Title,
            categoria = rule.Category,
            acao = rule.Action,
            resumo = rule.Summary,
            campos_obrigatorios = rule.RequiredFields.Select(field => field.Label).ToList(),
            fontes = rule.SourceReferences
        }).ToList();

        return Result(new
        {
            success = true,
            assunto = subject,
            acao = action,
            texto_pronto = body,
            campos_a_conferir = fields,
            regras_locais = ruleRows,
            fontes_legislacao = sources,
            observacao = sources.Count == 0
                ? "Não encontrei fonte jurídica indexada na biblioteca local para este assunto. O corpo foi montado pelo modelo administrativo do SIGFUR; importe a portaria/lei no módulo Legislação para citação literal."
                : "Texto montado com apoio da biblioteca local. Confira as páginas citadas antes de publicar."
        }, $"Corpo de boletim gerado: {subject} {action}".Trim());
    }

    private static string BuildLegislationQuery(string subject, string action, string query)
    {
        var normalized = Normalize(subject + " " + action + " " + query);
        if (normalized.Contains("FERIAS", StringComparison.Ordinal))
            return "férias adicional de férias militar remuneração pagamento Medida Provisória 2215-10 Decreto 4307 art. 80";
        if (normalized.Contains("AUXILIO TRANSPORTE", StringComparison.Ordinal) || normalized.Contains("TRANSPORTE", StringComparison.Ordinal))
            return "auxílio transporte militar pagamento cota parte Medida Provisória 2165-36 Portaria 849 Cmt Ex";
        if (normalized.Contains("PENSAO", StringComparison.Ordinal))
            return "pensão judicial militar desconto obrigatório Código de Processo Civil Medida Provisória 2215-10";
        if (normalized.Contains("GRATIFICACAO", StringComparison.Ordinal) || normalized.Contains("GRAT REP", StringComparison.Ordinal))
            return "gratificação de representação militar Decreto 11002 Portaria Exército pagamento";
        if (normalized.Contains("ALIMENTACAO", StringComparison.Ordinal))
            return "auxílio alimentação militar Decreto 4307 pagamento férias";
        return string.Join(' ', new[] { subject, action, query, "pagamento militar Exército portaria lei decreto" }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string BuildOperationalBulletinBody(
        string subject,
        string action,
        MilitaryRecord? military,
        int year,
        string period,
        string extra,
        IReadOnlyList<BulletinKnowledgeRule> rules,
        IReadOnlyList<LegislationSearchHit> legislationHits)
    {
        var normalized = Normalize(subject + " " + action + " " + extra);
        if (normalized.Contains("FERIAS", StringComparison.Ordinal))
            return BuildVacationBulletinBody(action, military, year, period, extra, legislationHits);

        var title = BuildBulletinTitle(subject, action);
        var identification = military is null
            ? "[P/G] [NOME COMPLETO], Nome de Guerra [NG], Prec-CP [PREC-CP], CPF [CPF]"
            : MilitaryIdentificationForBulletin(military);
        var rule = rules.FirstOrDefault();
        var legal = BuildLegalFoundationLine(subject, legislationHits);
        var required = rule is null || rule.RequiredFields.Count == 0
            ? "Dados específicos do lançamento: [informar conforme o tipo de direito, competência, valor, período, documento de amparo e motivo]."
            : "Dados específicos do lançamento: " + string.Join("; ", rule.RequiredFields.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase)) + ".";

        return $"{title}\n\n{legal} seja providenciado o lançamento administrativo referente a {subject.Trim().ToUpperInvariant()} {action.Trim().ToLowerInvariant()}, em favor do militar abaixo identificado, observadas as regras do SIPPES e a documentação de amparo correspondente.\n\nMilitar: {identification}.\n{required}\n{OptionalLine("Informações complementares", extra)}\n\nEm consequência, encaminhe-se ao setor competente para conferência dos dados, lançamento no sistema de pagamento e demais providências administrativas cabíveis.";
    }

    private static string BuildVacationBulletinBody(string action, MilitaryRecord? military, int year, string period, string extra, IReadOnlyList<LegislationSearchHit> legislationHits)
    {
        var normalizedAction = Normalize(action + " " + extra);
        var title = normalizedAction.Contains("ATRAS", StringComparison.Ordinal)
            ? "ADICIONAL DE FÉRIAS - Saque de Atrasados"
            : normalizedAction.Contains("CONCESS", StringComparison.Ordinal) || normalizedAction.Contains("GOZO", StringComparison.Ordinal) || normalizedAction.Contains("PEDIR", StringComparison.Ordinal)
                ? "FÉRIAS - Concessão"
                : "ADICIONAL DE FÉRIAS - Ordem de Saque";
        var referenceYear = year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "[ANO DE REFERÊNCIA]";
        var paymentYear = year > 1 ? (year - 1).ToString(CultureInfo.InvariantCulture) : "[ANO ANTERIOR, QUANDO APLICÁVEL]";
        var periodText = string.IsNullOrWhiteSpace(period) ? "[DATA DE INÍCIO] a [DATA DE TÉRMINO]" : period.Trim();
        var identification = military is null
            ? "[P/G] [NOME COMPLETO], Nome de Guerra [NG], Prec-CP [PREC-CP], CPF [CPF]"
            : MilitaryIdentificationForBulletin(military);
        var legal = legislationHits.Count > 0
            ? "Com fundamento na legislação de remuneração militar e nas normas locais indexadas na biblioteca do SIGFUR"
            : "Com fundamento na alínea “d” do inciso II do art. 2º da Medida Provisória nº 2.215-10, de 31 AGO 01, no Decreto nº 4.307, de 18 JUL 02, e nas normas administrativas vigentes do Plano de Férias";

        if (title.Equals("FÉRIAS - Concessão", StringComparison.OrdinalIgnoreCase))
        {
            return $"{title}\n\n{legal}, seja concedido o gozo de férias ao militar {identification}, por fazer jus ao período regulamentar de férias relativo ao ano de referência {referenceYear}, no período de {periodText}, totalizando [QUANTIDADE DE DIAS] dia(s), conforme planejamento da OM e disponibilidade do serviço.\n\nDocumento de amparo/publicação anterior: [BI/DIEx/Plano de Férias, se houver].\nObservação administrativa: quando a publicação gerar direito remuneratório, conferir se o adicional de férias deve entrar no pagamento do mês anterior ao início das férias, observando a corrida de pagamento correspondente.\n{OptionalLine("Informações complementares", extra)}\n\nEm consequência, sejam tomadas as providências de registro, controle e atualização no Plano de Férias/SIPPES, conforme o caso.";
        }

        if (title.Contains("Atrasados", StringComparison.OrdinalIgnoreCase))
        {
            return $"{title}\n\n{legal}, seja realizado o saque de atrasados do adicional de férias em favor do militar {identification}, relativo ao ano de referência {referenceYear}, tendo em vista que o direito não foi implantado ou não foi pago na competência própria.\n\nPeríodo de férias: {periodText}.\nQuantidade de dias: [QUANTIDADE DE DIAS].\nValor solicitado: [VALOR INDIVIDUAL CALCULADO NA CARTEIRA DE FÉRIAS].\nCompetência em que deveria ter sido pago: [PAGAMENTO {paymentYear}/MÊS CORRETO].\nDocumento de amparo: [BI Nr ___, de ___, da OM].\n{OptionalLine("Informações complementares", extra)}\n\nEm consequência, encaminhe-se ao setor competente para conferência do direito, lançamento no SIPPES e controle do pagamento em atraso.";
        }

        return $"{title}\n\n{legal}, seja realizado o saque do adicional de férias em favor do militar {identification}, por fazer jus ao direito remuneratório relativo ao ano de referência {referenceYear}, em virtude de férias previstas para o período de {periodText}.\n\nQuantidade de dias: [QUANTIDADE DE DIAS].\nPeríodo aquisitivo/ano de referência: {referenceYear}.\nCompetência operacional para pagamento: [PAGAMENTO DO MÊS ANTERIOR AO INÍCIO DAS FÉRIAS].\nDocumento de amparo: [BI Nr ___, de ___, da OM].\n{OptionalLine("Informações complementares", extra)}\n\nEm consequência, sejam adotadas as providências necessárias para lançamento no SIPPES, conferência na corrida de pagamento e controle do direito remuneratório.";
    }

    private static string MilitaryIdentificationForBulletin(MilitaryRecord military)
    {
        var rank = string.IsNullOrWhiteSpace(military.ShortRank) ? military.Rank : military.ShortRank;
        var name = string.IsNullOrWhiteSpace(military.Name) ? "[NOME COMPLETO]" : military.Name.ToUpperInvariant();
        var war = string.IsNullOrWhiteSpace(military.WarName) ? "[NG]" : military.WarName.ToUpperInvariant();
        var prec = string.IsNullOrWhiteSpace(military.PrecCp) ? "[PREC-CP]" : military.PrecCp;
        var cpf = string.IsNullOrWhiteSpace(military.FormattedCpf) ? "[CPF]" : military.FormattedCpf;
        return $"{rank} {name} (nome de guerra: {war}), Prec-CP {prec}, CPF {cpf}";
    }

    private static string BuildBulletinTitle(string subject, string action)
    {
        var left = string.IsNullOrWhiteSpace(subject) ? "PUBLICAÇÃO ADMINISTRATIVA" : subject.Trim().ToUpperInvariant();
        var right = string.IsNullOrWhiteSpace(action) ? "Ordem" : action.Trim();
        return left + " - " + CultureInfo.CurrentCulture.TextInfo.ToTitleCase(right.ToLowerInvariant());
    }

    private static string BuildLegalFoundationLine(string subject, IReadOnlyList<LegislationSearchHit> legislationHits)
    {
        if (legislationHits.Count > 0)
            return "Com fundamento na documentação normativa local indexada no SIGFUR, observadas as referências selecionadas pelo assistente,";
        var normalized = Normalize(subject);
        if (normalized.Contains("FERIAS", StringComparison.Ordinal))
            return "Com fundamento na alínea “d” do inciso II do art. 2º da Medida Provisória nº 2.215-10, de 31 AGO 01, no Decreto nº 4.307, de 18 JUL 02, e nas normas administrativas vigentes,";
        if (normalized.Contains("TRANSPORTE", StringComparison.Ordinal))
            return "Com fundamento na Medida Provisória nº 2.165-36, de 23 AGO 01, e nas normas de concessão do Auxílio-Transporte no âmbito do Exército,";
        if (normalized.Contains("PENSAO", StringComparison.Ordinal))
            return "Com fundamento na decisão judicial e nas normas de desconto obrigatório aplicáveis,";
        return "Com fundamento na legislação e nas normas administrativas aplicáveis,";
    }

    private static List<string> BuildBulletinMissingFields(string subject, string action, MilitaryRecord? military, int year, string period, string extra)
    {
        var fields = new List<string>();
        if (military is null) fields.Add("Identificação completa do militar: P/G, nome completo, nome de guerra, Prec-CP e CPF.");
        if (year <= 0) fields.Add("Ano de referência/ano do direito.");
        if (string.IsNullOrWhiteSpace(period)) fields.Add("Data de início e término ou período de referência.");
        if (string.IsNullOrWhiteSpace(extra)) fields.Add("Documento de amparo, quantidade de dias, valor/competência ou motivo, conforme o caso.");
        var normalized = Normalize(subject + " " + action);
        if (normalized.Contains("ATRAS", StringComparison.Ordinal)) fields.Add("Valor individual calculado e competência em que deixou de receber.");
        if (normalized.Contains("PENSAO", StringComparison.Ordinal)) fields.Add("Processo/decisão/ofício, alimentado, detentor da guarda e dados bancários quando aplicável.");
        return fields.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string OptionalLine(string label, string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : $"{label}: {value.Trim()}.";

    private async Task<AssistantToolExecutionResult> SearchMilitaryAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var query = GetString(args, "consulta");
        var limit = GetInt(args, "limite", 8, 1, 20);
        var rows = await _military.GetAllAsync(ct);
        var normalized = CleanMilitaryReference(query);
        var revealCpf = ShouldRevealSensitiveField(args, settings, "cpf");
        var revealPrec = ShouldRevealSensitiveField(args, settings, "prec_cp");
        var revealIdentity = ShouldRevealSensitiveField(args, settings, "identidade");
        var matches = rows
            .Select(x => new { Item = x, Score = ScoreMilitary(x, normalized) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => MilitaryRankService.GetOrder(x.Item.Rank))
            .ThenBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(x => new
            {
                id = x.Item.Id,
                posto_graduacao = x.Item.Rank,
                nome = x.Item.Name,
                nome_guerra = x.Item.WarName,
                formacao = x.Item.FormationYear,
                score = x.Score,
                cpf = revealCpf ? x.Item.FormattedCpf : null,
                prec_cp = revealPrec ? x.Item.PrecCp : null,
                identidade = revealIdentity ? x.Item.MilitaryId : null
            }).ToList();
        return Result(new { success = true, consulta = query, quantidade = matches.Count, resultados = matches }, $"Busca de militar: {matches.Count} resultado(s)");
    }

    private async Task<AssistantToolExecutionResult> GetMilitarySummaryAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var reference = GetString(args, "militar");
        var revealCpf = ShouldRevealSensitiveField(args, settings, "cpf");
        var revealPrec = ShouldRevealSensitiveField(args, settings, "prec_cp");
        var revealIdentity = ShouldRevealSensitiveField(args, settings, "identidade");
        var revealBirthDate = ShouldRevealSensitiveField(args, settings, "data_nascimento");
        var revealPhone = ShouldRevealSensitiveField(args, settings, "telefone");
        var revealEmail = ShouldRevealSensitiveField(args, settings, "email");
        var revealAddress = ShouldRevealSensitiveField(args, settings, "endereco");
        var military = await ResolveMilitaryAsync(reference, ct);
        if (military is null) return Result(new { success = false, error = "Militar não localizado ou consulta ambígua. Use buscar_militar primeiro." }, "Resumo militar: não localizado");
        var documents = await _military.GetDocumentsAsync(military.Id, ct);
        var transport = await _military.GetTransportSummaryAsync(military, ct);
        var route = await _military.GetTransportRouteDetailsAsync(military.Id, ct);
        return Result(new
        {
            success = true,
            militar = new
            {
                id = military.Id,
                posto_graduacao = military.Rank,
                nome = military.Name,
                nome_guerra = military.WarName,
                ano_formacao = military.FormationYear,
                data_praca = military.EnlistmentDate,
                tempo_servico = military.ServiceTimeText,
                data_nascimento = revealBirthDate ? military.BirthDate : null,
                recebe_auxilio_transporte = military.ReceivesTransportAid,
                valor_auxilio_transporte = military.TransportAidValue,
                pnr = military.HasPnr,
                adido_encostado = military.IsAttached,
                anotacao = settings.RedactSensitiveData ? AssistantAttachmentService.RedactSensitiveData(military.Annotation) : military.Annotation,
                documentos_cadastrados = documents.Count,
                cpf = revealCpf ? military.FormattedCpf : null,
                prec_cp = revealPrec ? military.PrecCp : null,
                identidade = revealIdentity ? military.MilitaryId : null,
                telefone = revealPhone ? military.Phone : null,
                email = revealEmail ? military.Email : null,
                endereco = revealAddress ? military.Address : null,
                auxilio_transporte = new
                {
                    recebe = military.ReceivesTransportAid,
                    valor_salvo = military.TransportAidValue,
                    origem = route.Origin,
                    destino = route.Destination,
                    dias_uteis = transport.WorkingDays,
                    bruto_por_dia = transport.GrossPerDay,
                    bruto_mensal = transport.GrossPerMonth,
                    cota_parte_6_porcento_proporcional = transport.Share,
                    liquido_mensal = transport.NetPerMonth,
                    quantidade_onibus = transport.Fares.Count,
                    linhas = transport.Fares.Select(x => new
                    {
                        ordem = x.Index + 1,
                        numero = x.Number,
                        nome_trajeto = x.Name,
                        categoria = x.Category,
                        tarifa_ida = x.Fare,
                        ida_e_volta = x.Fare * 2,
                        fonte = x.SourceUrl
                    }).ToList()
                }
            }
        }, $"Resumo de {military.ShortRank} {military.WarName}");
    }


    private async Task<AssistantToolExecutionResult> CountPersonnelAsync(JsonObject args, CancellationToken ct)
    {
        var filter = Normalize(GetString(args, "filtro"));
        var includeLt = GetBool(args, "incluir_licenciados_transferidos");
        var rows = await _military.GetAllAsync(ct);
        var filtered = rows.Where(x => string.IsNullOrWhiteSpace(filter) || Normalize(string.Join(" ", x.Rank, x.ShortRank, x.Name, x.WarName, x.FormationYear, x.Annotation)).Contains(filter, StringComparison.Ordinal)).ToList();
        var byRank = filtered
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Rank) ? "Sem posto/graduação" : x.Rank)
            .OrderBy(x => MilitaryRankService.GetOrder(x.Key))
            .ThenBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new { posto_graduacao = x.Key, quantidade = x.Count() })
            .ToList();
        var ltCount = 0;
        if (includeLt)
        {
            try { ltCount = (await _licensedTransferred.GetAllAsync(includeHidden: true, filter: string.Empty, cancellationToken: ct)).Count; }
            catch { ltCount = 0; }
        }
        var indicadores = new
        {
            pre_escolar = filtered.Count(x => MilitaryRecord.IsYes(x.ReceivesPreSchool)),
            auxilio_transporte = filtered.Count(x => MilitaryRecord.IsYes(x.ReceivesTransportAid)),
            pensao_judicial = filtered.Count(x => MilitaryRecord.IsYes(x.Alimony)),
            pnr = filtered.Count(x => MilitaryRecord.IsYes(x.HasPnr)),
            laranjeira = filtered.Count(x => x.IsOrange),
            adido_encostado = filtered.Count(x => x.IsAttached),
            sem_cpf = filtered.Count(x => string.IsNullOrWhiteSpace(x.Cpf)),
            sem_prec_cp = filtered.Count(x => string.IsNullOrWhiteSpace(x.PrecCp))
        };
        return Result(new
        {
            success = true,
            filtro = GetString(args, "filtro"),
            total_cadastrado_ativo = rows.Count,
            total_filtrado = filtered.Count,
            total_licenciados_transferidos = includeLt ? ltCount : (int?)null,
            total_geral_com_licenciados_transferidos = includeLt ? rows.Count + ltCount : (int?)null,
            por_posto_graduacao = byRank,
            indicadores
        }, $"Efetivo SIGFUR: {filtered.Count} de {rows.Count} ativo(s)");
    }

    private async Task<AssistantToolExecutionResult> GetOperationalMilitaryContextAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var reference = GetString(args, "militar");
        var year = GetInt(args, "ano", DateTime.Today.Year, 2000, 2100);
        var month = GetInt(args, "mes", DateTime.Today.Month, 1, 12);
        var subjects = GetString(args, "assuntos");
        var limit = GetInt(args, "limite_boletins", 20, 1, 50);
        var military = await ResolveMilitaryAsync(reference, ct);
        if (military is null) return Result(new { success = false, error = "Militar não localizado ou consulta ambígua. Use buscar_militar primeiro." }, "Consulta operacional: militar não localizado");

        var vacationPeriods = await _vacations.GetPeriodsAsync(year, ct);
        var allocations = await _vacations.GetAllocationsForMilitaryAsync(year, military, ct);
        var periodLookup = vacationPeriods.ToDictionary(x => x.Id);
        var vacations = allocations.Select(x =>
        {
            periodLookup.TryGetValue(x.PeriodId, out var period);
            return new
            {
                periodo = period?.DisplayName ?? $"Período {x.PeriodId}",
                inicio = period?.StartDate?.ToString("dd/MM/yyyy"),
                termino = period?.EndDate?.ToString("dd/MM/yyyy"),
                dias = x.Days,
                adicional_ferias_pago = x.IsPaid,
                aux_alimentacao_ferias_pago = x.FoodAidPaid,
                pago_em = x.PaidAt?.ToString("dd/MM/yyyy HH:mm")
            };
        }).ToList();

        var transport = await _military.GetTransportSummaryAsync(military, ct);
        var route = await _military.GetTransportRouteDetailsAsync(military.Id, ct);
        var documents = await _military.GetDocumentsAsync(military.Id, ct);
        var paystubs = (await _paystubs.FindForMilitaryAsync(military, ct)).OrderByDescending(x => x.ModifiedAt).Take(12).Select(x => new
        {
            competencia = x.Reference,
            arquivo = x.FileName,
            tipo = x.DocumentType,
            alterado_em = x.ModifiedAt.ToString("dd/MM/yyyy HH:mm"),
            disponivel = File.Exists(x.Path)
        }).ToList();
        var reminders = (await _reminders.LoadAsync(normalizeRecurring: true, cancellationToken: ct))
            .Where(x => !x.Completed && Normalize(x.Title + " " + x.Body).Contains(Normalize(military.Name), StringComparison.Ordinal) || !x.Completed && !string.IsNullOrWhiteSpace(military.WarName) && Normalize(x.Title + " " + x.Body).Contains(Normalize(military.WarName), StringComparison.Ordinal))
            .OrderBy(x => x.DueDate ?? DateTime.MaxValue)
            .Take(10)
            .Select(x => new { titulo = x.Title, data = x.FormattedDate, prazo = x.DaysText, prioridade = x.EffectivePriority, descricao = TrimText(x.Body, 400) })
            .ToList();

        var absences = (await _absences.ListAsync(year, month, military.Id)).OrderByDescending(x => x.Date).Take(10).Select(x => new
        {
            data = x.DateText,
            horario = x.Time,
            tipo = x.Type,
            minutos = x.Minutes,
            justificada = x.Justified,
            medida = x.Measure
        }).ToList();

        var bulletins = await SearchAllBulletinSourcesAsync(military, reference, subjects, limit, ct);

        return Result(new
        {
            success = true,
            consulta = new { ano = year, mes = month, assuntos = subjects },
            militar = new
            {
                id = military.Id,
                posto_graduacao = military.Rank,
                nome = military.Name,
                nome_guerra = military.WarName,
                cpf = military.FormattedCpf,
                prec_cp = military.PrecCp,
                identidade = military.MilitaryId,
                formacao = military.FormationYear,
                data_praca = military.EnlistmentDate,
                tempo_servico = military.ServiceTimeText,
                pre_escolar = military.ReceivesPreSchool,
                valor_pre_escolar = military.PreSchoolValue,
                auxilio_transporte = military.ReceivesTransportAid,
                valor_auxilio_transporte = military.TransportAidValue,
                pnr = military.HasPnr,
                pensao_judicial = military.Alimony,
                valor_pensao = military.AlimonyValue,
                laranjeira = military.IsOrange,
                adido_encostado = military.IsAttached
            },
            ferias = new { total_dias = allocations.Sum(x => x.Days), periodos = vacations },
            auxilio_transporte = new
            {
                recebe = military.ReceivesTransportAid,
                origem = route.Origin,
                destino = route.Destination,
                dias_uteis = transport.WorkingDays,
                bruto_por_dia = transport.GrossPerDay,
                bruto_mensal = transport.GrossPerMonth,
                cota_parte = transport.Share,
                liquido_mensal = transport.NetPerMonth,
                linhas = transport.Fares.Select(x => new { numero = x.Number, nome_trajeto = x.Name, tarifa = x.Fare }).ToList()
            },
            boletins = bulletins,
            documentos = documents.Select(x => new { tipo = x.Type, titulo = x.Title, arquivo = x.FileName, salvo_em = x.SavedAt, disponivel = x.Exists, ocr = x.OcrStatus }).ToList(),
            contracheques = paystubs,
            lembretes = reminders,
            faltas_atrasos = absences
        }, $"Consulta operacional de {military.ShortRank} {military.WarName}: {bulletins.Count} menção(ões), {vacations.Count} período(s) de férias");
    }

    private async Task<AssistantToolExecutionResult> GetTransportAidAsync(JsonObject args, CancellationToken ct)
    {
        var reference = GetString(args, "militar");
        var military = await ResolveMilitaryAsync(reference, ct);
        if (military is null)
            return Result(new { success = false, error = "Militar não localizado ou consulta ambígua." }, "Auxílio-Transporte: militar não localizado");

        var summary = await _military.GetTransportSummaryAsync(military, ct);
        var route = await _military.GetTransportRouteDetailsAsync(military.Id, ct);
        var buses = summary.Fares.Select(x => new
        {
            ordem = x.Index + 1,
            numero = x.Number,
            nome_trajeto = x.Name,
            categoria = x.Category,
            tarifa_ida = x.Fare,
            ida_e_volta = x.Fare * 2,
            fonte = x.SourceUrl
        }).ToList();

        return Result(new
        {
            success = true,
            militar = new { id = military.Id, posto_graduacao = military.Rank, nome = military.Name, nome_guerra = military.WarName },
            recebe = military.ReceivesTransportAid,
            valor_salvo = military.TransportAidValue,
            rota = new { origem = route.Origin, destino = route.Destination, descricao = route.RouteDescription },
            quantidade_onibus = buses.Count,
            linhas = buses,
            calculo = new
            {
                soldo = summary.Salary,
                dias_uteis = summary.WorkingDays,
                bruto_por_dia = summary.GrossPerDay,
                bruto_mensal = summary.GrossPerMonth,
                cota_parte_6_porcento_proporcional = summary.Share,
                liquido_mensal = summary.NetPerMonth,
                bloqueado_adido_encostado = summary.BlockedByAttachedStatus
            },
            observacao = buses.Count == 0 ? "Nenhuma linha detalhada foi localizada nos bancos de tarifas e rotas." : string.Empty
        }, $"Auxílio-Transporte de {military.ShortRank} {military.WarName}: {buses.Count} linha(s)");
    }

    private async Task<AssistantToolExecutionResult> GetVacationsAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var reference = GetString(args, "militar");
        var year = GetInt(args, "ano", DateTime.Today.Year, 2000, 2100);
        var military = await ResolveMilitaryAsync(reference, ct);
        if (military is null) return Result(new { success = false, error = "Militar não localizado ou consulta ambígua." }, "Férias: militar não localizado");
        var periods = await _vacations.GetPeriodsAsync(year, ct);
        var allocations = await _vacations.GetAllocationsForMilitaryAsync(year, military, ct);
        var periodLookup = periods.ToDictionary(x => x.Id);
        var rows = allocations.Select(x =>
        {
            periodLookup.TryGetValue(x.PeriodId, out var period);
            var start = period?.StartDate;
            DateTime? paymentCompetence = start is null ? null : new DateTime(start.Value.Year, start.Value.Month, 1).AddMonths(-1);
            DateTime? firstRun = paymentCompetence is null ? null : new DateTime(paymentCompetence.Value.Year, paymentCompetence.Value.Month, 15).AddMonths(-1);
            DateTime? secondRun = paymentCompetence is null ? null : ThirdBusinessDay(paymentCompetence.Value.Year, paymentCompetence.Value.Month);
            var status = firstRun is null || secondRun is null
                ? (x.IsPaid ? "Pago no SIGFUR" : "Pendente — período sem data inicial")
                : PaymentVacationStatus(x, firstRun.Value, secondRun.Value, DateTime.Today).Text;
            return new
            {
                periodo = period?.DisplayName ?? $"Período {x.PeriodId}",
                inicio = start?.ToString("dd/MM/yyyy"),
                termino = period?.EndDate?.ToString("dd/MM/yyyy"),
                dias = x.Days,
                ano_referencia_ferias = year - 1,
                pagamento_deve_entrar_em = paymentCompetence is null ? string.Empty : $"Pagamento {MonthName(paymentCompetence.Value.Month)} {paymentCompetence.Value.Year:0000}",
                primeira_corrida = firstRun is null ? string.Empty : FormatMilitaryDate(firstRun.Value),
                segunda_corrida = secondRun is null ? string.Empty : FormatMilitaryDate(secondRun.Value),
                status_corrida = status,
                adicional_pago = x.IsPaid,
                pago_em = x.PaidAt?.ToString("dd/MM/yyyy HH:mm"),
                aux_alimentacao_aplica = x.RequiresVacationFoodAid,
                aux_alimentacao_pago = x.FoodAidPaid
            };
        }).ToList();
        return Result(new
        {
            success = true,
            ano = year,
            militar = new { id = military.Id, posto_graduacao = military.Rank, nome = military.Name, nome_guerra = military.WarName },
            total_dias = allocations.Sum(x => x.Days),
            alocacoes = rows,
            observacao = rows.Count == 0 ? "Não há férias cadastradas para este militar no ano informado." : string.Empty
        }, $"Férias de {military.ShortRank} {military.WarName}: {rows.Count} período(s)");
    }


    public async Task<string> BuildPaymentRunAlertContextAsync(CancellationToken ct)
    {
        try
        {
            var today = DateTime.Today;
            var alerts = await BuildVacationPaymentAlertsAsync(0, 0, false, 12, ct);
            var urgent = alerts
                .Where(x => !x.IsPaid && (x.StatusRank <= 2 || x.PaymentSecondRun <= today.AddDays(45)))
                .OrderBy(x => x.StatusRank)
                .ThenBy(x => x.PaymentSecondRun)
                .ThenBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
                .ThenBy(x => x.Military.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(8)
                .ToList();
            if (urgent.Count == 0)
                return "CONTEXTO OPERACIONAL DE FÉRIAS: nenhuma pendência crítica de pagamento de férias localizada para os ciclos atuais/próximos.";

            var builder = new StringBuilder();
            builder.AppendLine("CONTEXTO OPERACIONAL DE FÉRIAS PARA O ASSISTENTE");
            builder.AppendLine("Regra operacional configurada: férias com início em determinado mês devem entrar no pagamento do mês anterior; 1ª corrida sugerida no dia 15 do mês anterior à competência e 2ª corrida no 3º dia útil da competência.");
            builder.AppendLine("Ao responder perguntas administrativas, depois de responder a pergunta principal, acrescente um bloco curto 'Atenção da corrida de pagamento' se houver pendência abaixo.");
            foreach (var alert in urgent)
            {
                builder.AppendLine($"• {alert.Status}: {alert.Military.ShortRank} {alert.Military.WarName} — férias {alert.VacationStartText} a {alert.VacationEndText}, {alert.Days} dia(s), {alert.PaymentReference}, 1ª corrida {alert.PaymentFirstRunText}, 2ª corrida {alert.PaymentSecondRunText}, adicional {(alert.IsPaid ? "pago" : "pendente")}{(alert.RequiresFoodAid ? ", aux. alimentação " + (alert.FoodAidPaid ? "pago" : "pendente") : string.Empty)}.");
            }
            return builder.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<AssistantToolExecutionResult> GetPaymentRunVacationAlertsAsync(JsonObject args, CancellationToken ct)
    {
        var year = GetInt(args, "ano_pagamento", 0, 0, 2100);
        var month = GetInt(args, "mes_pagamento", 0, 0, 12);
        var limit = GetInt(args, "limite", 30, 1, 80);
        var includePaid = GetBool(args, "incluir_pagos");
        var alerts = await BuildVacationPaymentAlertsAsync(year, month, includePaid, limit, ct);
        var today = DateTime.Today;
        var rows = alerts
            .OrderBy(x => x.StatusRank)
            .ThenBy(x => x.PaymentSecondRun)
            .ThenBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
            .ThenBy(x => x.Military.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(x => new
            {
                status = x.Status,
                prioridade = x.Priority,
                militar = $"{x.Military.ShortRank} {x.Military.Name}".Trim(),
                nome_guerra = x.Military.WarName,
                plano_ferias = x.PlanYear,
                ano_referencia_ferias = x.PlanYear - 1,
                periodo = x.PeriodName,
                inicio_ferias = x.VacationStartText,
                termino_ferias = x.VacationEndText,
                dias = x.Days,
                pagamento = x.PaymentReference,
                primeira_corrida = x.PaymentFirstRunText,
                segunda_corrida = x.PaymentSecondRunText,
                adicional_ferias = x.IsPaid ? "Pago" : "Pendente",
                pago_em = x.PaidAtText,
                aux_alimentacao_aplica = x.RequiresFoodAid,
                aux_alimentacao = x.RequiresFoodAid ? (x.FoodAidPaid ? "Pago" : "Pendente") : "Não se aplica",
                observacao = x.Note
            })
            .ToList();

        var summary = new
        {
            pendentes = alerts.Count(x => !x.IsPaid),
            atrasados = alerts.Count(x => !x.IsPaid && x.PaymentSecondRun.Date < today),
            em_corrida = alerts.Count(x => !x.IsPaid && x.PaymentFirstRun.Date <= today && today <= x.PaymentSecondRun.Date),
            proximos = alerts.Count(x => !x.IsPaid && x.PaymentFirstRun.Date > today),
            aux_alimentacao_pendente = alerts.Count(x => x.RequiresFoodAid && !x.FoodAidPaid)
        };
        var scope = year > 0 && month > 0 ? $"{MonthName(month)} {year:0000}" : "ciclos atuais/próximos";
        return Result(new
        {
            success = true,
            regra = "Férias que iniciam em um mês entram no pagamento do mês anterior; 1ª corrida no dia 15 do mês anterior à competência e 2ª corrida no 3º dia útil da competência.",
            escopo = scope,
            resumo = summary,
            quantidade = rows.Count,
            alertas = rows
        }, $"Alertas de férias/corrida de pagamento: {rows.Count} item(ns)");
    }

    private async Task<List<VacationPaymentAlert>> BuildVacationPaymentAlertsAsync(int paymentYear, int paymentMonth, bool includePaid, int limit, CancellationToken ct)
    {
        var today = DateTime.Today;
        var years = new HashSet<int>();
        if (paymentYear > 0 && paymentMonth > 0)
        {
            var vacationMonth = new DateTime(paymentYear, paymentMonth, 1).AddMonths(1);
            years.Add(vacationMonth.Year);
        }
        else
        {
            for (var y = today.Year - 1; y <= today.Year + 2; y++) years.Add(y);
        }

        var result = new List<VacationPaymentAlert>();
        foreach (var year in years.OrderBy(x => x))
        {
            var periods = await _vacations.GetPeriodsAsync(year, ct);
            var allocations = await _vacations.GetAllocationsAsync(year, null, ct);
            var periodLookup = periods.ToDictionary(x => x.Id);
            foreach (var allocation in allocations)
            {
                if (!periodLookup.TryGetValue(allocation.PeriodId, out var period)) continue;
                var start = period.StartDate;
                if (start is null) continue;
                var end = period.EndDate ?? start.Value.AddDays(Math.Max(1, allocation.Days) - 1);
                var paymentCompetence = new DateTime(start.Value.Year, start.Value.Month, 1).AddMonths(-1);
                if (paymentYear > 0 && paymentMonth > 0 && (paymentCompetence.Year != paymentYear || paymentCompetence.Month != paymentMonth)) continue;
                if (paymentYear == 0 || paymentMonth == 0)
                {
                    if (paymentCompetence < new DateTime(today.Year, today.Month, 1).AddMonths(-2) || paymentCompetence > new DateTime(today.Year, today.Month, 1).AddMonths(4))
                        continue;
                    if (allocation.IsPaid && !includePaid) continue;
                    if (allocation.IsPaid && allocation.PaidAt is not null && allocation.PaidAt.Value.Date < today.AddDays(-45) && !includePaid) continue;
                }
                else if (allocation.IsPaid && !includePaid)
                {
                    continue;
                }

                var firstRun = new DateTime(paymentCompetence.Year, paymentCompetence.Month, 15).AddMonths(-1);
                var secondRun = ThirdBusinessDay(paymentCompetence.Year, paymentCompetence.Month);
                var status = PaymentVacationStatus(allocation, firstRun, secondRun, today);
                var note = allocation.IsPaid
                    ? "Marcado como pago no Plano de Férias. Conferir se constou no contracheque quando necessário."
                    : today > secondRun.Date
                        ? "Passou da 2ª corrida e o adicional continua pendente no Plano de Férias. Prioridade alta para conferir publicação/contracheque."
                        : today >= firstRun.Date
                            ? "Está dentro da janela de corrida. Se ainda não foi enviado ao SIPPES, publicar/enviar agora."
                            : "Ainda antes da 1ª corrida. Deixar programado para não esquecer.";

                result.Add(new VacationPaymentAlert
                {
                    Military = allocation.Military,
                    Allocation = allocation,
                    Period = period,
                    PlanYear = year,
                    PeriodName = period.DisplayName,
                    VacationStart = start.Value,
                    VacationEnd = end,
                    Days = allocation.Days,
                    PaymentCompetence = paymentCompetence,
                    PaymentFirstRun = firstRun,
                    PaymentSecondRun = secondRun,
                    Status = status.Text,
                    StatusRank = status.Rank,
                    Priority = status.Priority,
                    Note = note
                });
            }
        }
        return result
            .OrderBy(x => x.StatusRank)
            .ThenBy(x => x.PaymentSecondRun)
            .ThenBy(x => MilitaryRankService.GetOrder(x.Military.Rank))
            .ThenBy(x => x.Military.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(limit, 1) * 3)
            .ToList();
    }

    private static (string Text, int Rank, string Priority) PaymentVacationStatus(VacationAllocation allocation, DateTime firstRun, DateTime secondRun, DateTime today)
    {
        if (allocation.IsPaid) return ("Pago no SIGFUR", 5, "Baixa");
        if (today.Date > secondRun.Date) return ("Atrasado após a 2ª corrida", 1, "Urgentíssimo");
        if (today.Date >= firstRun.Date) return ("Dentro da corrida de pagamento", 2, "Urgente");
        if (today.Date >= firstRun.Date.AddDays(-15)) return ("Pré-corrida próxima", 3, "Alta");
        return ("Programado", 4, "Normal");
    }

    private sealed class VacationPaymentAlert
    {
        public MilitaryRecord Military { get; init; } = new();
        public VacationAllocation Allocation { get; init; } = new();
        public VacationPeriod Period { get; init; } = new();
        public int PlanYear { get; init; }
        public string PeriodName { get; init; } = string.Empty;
        public DateTime VacationStart { get; init; }
        public DateTime VacationEnd { get; init; }
        public int Days { get; init; }
        public DateTime PaymentCompetence { get; init; }
        public DateTime PaymentFirstRun { get; init; }
        public DateTime PaymentSecondRun { get; init; }
        public string Status { get; init; } = string.Empty;
        public int StatusRank { get; init; }
        public string Priority { get; init; } = string.Empty;
        public string Note { get; init; } = string.Empty;
        public bool IsPaid => Allocation.IsPaid;
        public bool FoodAidPaid => Allocation.FoodAidPaid;
        public bool RequiresFoodAid => Allocation.RequiresVacationFoodAid;
        public string PaidAtText => Allocation.PaidAt is null ? string.Empty : Allocation.PaidAt.Value.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
        public string VacationStartText => VacationStart.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
        public string VacationEndText => VacationEnd.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR"));
        public string PaymentFirstRunText => FormatMilitaryDate(PaymentFirstRun);
        public string PaymentSecondRunText => FormatMilitaryDate(PaymentSecondRun);
        public string PaymentReference => $"Pagamento {MonthName(PaymentCompetence.Month)} {PaymentCompetence.Year:0000}";
    }

    private static DateTime ThirdBusinessDay(int year, int month)
    {
        var day = new DateTime(year, month, 1);
        var count = 0;
        while (day.Month == month)
        {
            if (IsBusinessDay(day))
            {
                count++;
                if (count == 3) return day;
            }
            day = day.AddDays(1);
        }
        return new DateTime(year, month, Math.Min(5, DateTime.DaysInMonth(year, month)));
    }

    private static bool IsBusinessDay(DateTime date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var fixedHoliday = (date.Month, date.Day);
        if (fixedHoliday is (1, 1) or (4, 21) or (5, 1) or (9, 7) or (10, 12) or (11, 2) or (11, 15) or (12, 25)) return false;
        var easter = EasterSunday(date.Year);
        if (date.Date == easter.AddDays(-2).Date || date.Date == easter.AddDays(60).Date) return false;
        return true;
    }

    private static DateTime EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }

    private static string MonthName(int month)
    {
        var names = new[] { "JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO", "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO" };
        return month is >= 1 and <= 12 ? names[month - 1] : string.Empty;
    }

    private static string FormatMilitaryDate(DateTime date)
    {
        var names = new[] { "JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ" };
        return $"{date:dd} {names[date.Month - 1]} {date:yy}";
    }

    private async Task<AssistantToolExecutionResult> SearchBulletinsAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var person = GetString(args, "militar");
        var subject = GetString(args, "assunto");
        var limit = GetInt(args, "limite", 12, 1, 30);
        var personNorm = Normalize(person);
        var subjectNorm = Normalize(subject);
        if (string.IsNullOrWhiteSpace(personNorm) && string.IsNullOrWhiteSpace(subjectNorm))
            return Result(new { success = false, error = "Informe um militar ou assunto para pesquisar." }, "Boletins: consulta vazia");

        var store = await _bulletins.LoadAsync(ct);
        var selectedFindings = store.Items
            .SelectMany(file => file.Findings.Select(finding => new { File = file, Finding = finding }))
            .Where(x => MatchesBulletinFinding(x.Finding, personNorm, subjectNorm))
            .OrderByDescending(x => ParseDate(x.File.DateIso) ?? DateTime.MinValue)
            .ThenByDescending(x => BulletinNumber(x.File.BulletinNumber))
            .Take(limit)
            .ToList();

        var actions = new List<AssistantPendingAction>();
        var findings = selectedFindings
            .Select(x =>
            {
                var bi = x.Finding.Bulletin == "—" ? x.File.BulletinNumber : x.Finding.Bulletin;
                var data = x.Finding.BulletinDate == "—" ? x.File.BulletinDate : x.Finding.BulletinDate;
                var assunto = string.IsNullOrWhiteSpace(x.Finding.DisplaySubject) ? x.Finding.Type : x.Finding.DisplaySubject;
                var path = FirstExistingPath(x.Finding.PdfPath, x.File.PdfPath);
                if (!string.IsNullOrWhiteSpace(path))
                    actions.Add(AssistantActionRegistry.OpenFile(BuildBulletinAssistantLabel(bi, data, assunto, x.Finding.Page), path, $"Abrir PDF salvo: {Path.GetFileName(path)}"));
                return new
                {
                    bi,
                    data,
                    pagina = x.Finding.Page,
                    categoria = x.Finding.Category,
                    tipo = x.Finding.Type,
                    militar = x.Finding.DisplayMilitary,
                    assunto_nota = assunto,
                    detalhe = TrimText(x.Finding.Detail, 900),
                    contexto = TrimText(x.Finding.Context, 1_500),
                    arquivo = x.File.FileName
                };
            }).ToList();

        if (findings.Count == 0)
        {
            var fallback = new List<object>();
            foreach (var file in store.Items.OrderByDescending(x => ParseDate(x.DateIso) ?? DateTime.MinValue))
            {
                if (fallback.Count >= limit) break;
                var text = await _bulletins.ReadCachedTextAsync(file, ct);
                var snippet = FindSnippet(text, personNorm, subjectNorm, 1_200);
                if (string.IsNullOrWhiteSpace(snippet)) continue;
                var path = FirstExistingPath(file.PdfPath);
                if (!string.IsNullOrWhiteSpace(path))
                    actions.Add(AssistantActionRegistry.OpenFile(BuildBulletinAssistantLabel(file.BulletinNumber, file.BulletinDate, subject, 0), path, $"Abrir PDF salvo: {Path.GetFileName(path)}"));
                fallback.Add(new { bi = file.BulletinNumber, data = file.BulletinDate, pagina = 0, categoria = "Texto integral", tipo = "Ocorrência textual", militar = person, detalhe = snippet, contexto = snippet, arquivo = file.FileName });
            }
            return Result(new { success = true, quantidade = fallback.Count, resultados = fallback, fonte = "texto indexado dos boletins" }, $"Boletins: {fallback.Count} ocorrência(s) textuais", actions);
        }

        return Result(new { success = true, quantidade = findings.Count, resultados = findings, fonte = "achados estruturados do índice de boletins" }, $"Boletins: {findings.Count} achado(s)", actions);
    }

    private static string FirstExistingPath(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
        }
        return string.Empty;
    }

    private static string BuildBulletinAssistantLabel(string bulletin, string date, string subject, int page)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(bulletin) && bulletin != "—")
            parts.Add(bulletin.StartsWith("BI", StringComparison.OrdinalIgnoreCase) ? bulletin : "BI " + bulletin);
        if (!string.IsNullOrWhiteSpace(date) && date != "—") parts.Add(date);
        if (!string.IsNullOrWhiteSpace(subject) && subject != "—") parts.Add(subject.Trim());
        if (page > 0) parts.Add("pág. " + page.ToString(CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "Abrir boletim" : string.Join(" — ", parts);
    }

    private async Task<AssistantToolExecutionResult> GetMilitaryDocumentsAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var military = await ResolveMilitaryAsync(GetString(args, "militar"), ct);
        if (military is null) return Result(new { success = false, error = "Militar não localizado ou consulta ambígua." }, "Documentos: militar não localizado");
        var documents = await _military.GetDocumentsAsync(military.Id, ct);
        var rows = documents.Select(x => new
        {
            tipo = x.Type,
            titulo = x.Title,
            arquivo = x.FileName,
            salvo_em = x.SavedAt,
            observacao = settings.RedactSensitiveData ? AssistantAttachmentService.RedactSensitiveData(x.Observation) : x.Observation,
            disponivel = x.Exists,
            caminho = x.Exists ? x.Path : string.Empty,
            ocr = x.OcrStatus
        }).ToList();
        var actions = new List<AssistantPendingAction> { AssistantActionRegistry.OpenWallet(military) };
        foreach (var doc in documents.Where(x => x.Exists).Take(6))
            actions.Add(AssistantActionRegistry.OpenFile($"Abrir {doc.Title}", doc.Path, $"Abrir documento salvo: {doc.FileName}"));
        return Result(new { success = true, militar = $"{military.ShortRank} {military.Name}", quantidade = rows.Count, documentos = rows }, $"Documentos de {military.WarName}: {rows.Count}", actions);
    }

    private async Task<AssistantToolExecutionResult> SearchLegislationAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var query = GetString(args, "consulta");
        var limit = GetInt(args, "limite", 10, 1, 20);
        var hits = await _legislation.SearchAsync(query, limit);
        var rows = hits.Select(x => new { referencia = x.Reference, titulo = x.Title, pagina = x.Page, trecho = TrimText(x.Snippet, 1_500), score = x.Score }).ToList();
        var answer = rows.Count == 0 ? string.Empty : TrimText(await _legislation.AnswerAsync(query, hits), 8_000);
        return Result(new
        {
            success = true,
            consulta = query,
            quantidade = rows.Count,
            resultados = rows,
            resposta_offline = answer,
            aviso = rows.Count == 0 ? "Não há base local suficiente. Não invente norma ou artigo; oriente importar/indexar a norma em Legislação." : "Use apenas as referências retornadas. Se o trecho exato não aparecer, peça conferência na fonte original antes de enviar documento oficial."
        }, $"Legislação: {rows.Count} referência(s)");
    }

    private async Task<AssistantToolExecutionResult> GetPaystubsAsync(JsonObject args, CancellationToken ct)
    {
        var military = await ResolveMilitaryAsync(GetString(args, "militar"), ct);
        if (military is null) return Result(new { success = false, error = "Militar não localizado ou consulta ambígua." }, "Contracheques: militar não localizado");
        var limit = GetInt(args, "limite", 12, 1, 24);
        var files = await _paystubs.FindForMilitaryAsync(military, ct);
        var selected = files
            .OrderByDescending(x => x.ModifiedAt)
            .Take(limit)
            .ToList();
        var rows = selected
            .Select(x => new
            {
                competencia = x.Reference,
                arquivo = x.FileName,
                alterado_em = x.ModifiedAt.ToString("dd/MM/yyyy HH:mm"),
                tamanho = x.SizeText,
                disponivel = File.Exists(x.Path),
                caminho = File.Exists(x.Path) ? x.Path : string.Empty
            }).ToList();
        var actions = new List<AssistantPendingAction> { AssistantActionRegistry.OpenWallet(military) };
        foreach (var item in selected.Where(x => File.Exists(x.Path)).Take(4))
        {
            actions.Add(AssistantActionRegistry.OpenFile($"Abrir {item.Reference}", item.Path, $"Abrir contracheque salvo: {item.FileName}"));
            actions.Add(AssistantActionRegistry.PrintFile($"Imprimir {item.Reference}", item.Path, 1, $"Preparar impressão: {item.FileName}"));
        }
        return Result(new
        {
            success = true,
            militar = $"{military.ShortRank} {military.Name}",
            quantidade = rows.Count,
            contracheques = rows,
            observacao = rows.Count == 0 ? "Nenhum contracheque salvo foi localizado para o militar." : "Foram geradas ações para abrir/imprimir os principais arquivos encontrados."
        }, $"Contracheques de {military.WarName}: {rows.Count}", actions);
    }

    private async Task<AssistantToolExecutionResult> SearchLicensedTransferredAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var query = GetString(args, "consulta");
        var limit = GetInt(args, "limite", 10, 1, 20);
        var revealCpf = ShouldRevealSensitiveField(args, settings, "cpf");
        var revealPrec = ShouldRevealSensitiveField(args, settings, "prec_cp");
        var rows = await _licensedTransferred.GetAllAsync(includeHidden: true, filter: query, cancellationToken: ct);
        var matches = rows.Take(limit).Select(x => new
        {
            id = x.Id,
            posto_graduacao = x.Rank,
            nome = x.Name,
            nome_guerra = x.WarName,
            motivo = x.Reason,
            destino = x.Destination,
            situacao = x.StatusText,
            ano_formacao = x.FormationYear,
            cpf = revealCpf ? x.FormattedCpf : null,
            prec_cp = revealPrec ? x.FormattedPrecCp : null
        }).ToList();
        return Result(new { success = true, consulta = query, quantidade = matches.Count, resultados = matches }, $"Licenciados/transferidos: {matches.Count} resultado(s)");
    }

    private async Task<AssistantToolExecutionResult> GetRemindersAsync(JsonObject args, CancellationToken ct)
    {
        var query = Normalize(GetString(args, "consulta"));
        var includeCompleted = GetBool(args, "incluir_concluidos");
        var limit = GetInt(args, "limite", 12, 1, 30);
        var rows = await _reminders.LoadAsync(normalizeRecurring: true, cancellationToken: ct);
        var matches = rows
            .Where(x => includeCompleted || !x.Completed)
            .Where(x => string.IsNullOrWhiteSpace(query) || Normalize($"{x.Title} {x.Body} {x.Priority} {x.Status}").Contains(query, StringComparison.Ordinal))
            .OrderBy(x => x.Completed)
            .ThenBy(x => x.DueDate ?? DateTime.MaxValue)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(x => new
            {
                titulo = x.Title,
                data = x.FormattedDate,
                prazo = x.DaysText,
                prioridade = x.EffectivePriority,
                situacao = x.Status,
                concluido = x.Completed,
                recorrencia = x.Recurrence,
                descricao = TrimText(x.Body, 700)
            }).ToList();
        return Result(new { success = true, quantidade = matches.Count, lembretes = matches }, $"Lembretes: {matches.Count} resultado(s)");
    }

    private async Task<AssistantToolExecutionResult> GetAbsencesAsync(JsonObject args, CancellationToken ct)
    {
        var military = await ResolveMilitaryAsync(GetString(args, "militar"), ct);
        if (military is null) return Result(new { success = false, error = "Militar não localizado ou consulta ambígua." }, "Faltas/atrasos: militar não localizado");
        var year = GetInt(args, "ano", DateTime.Today.Year, 2000, 2100);
        var month = GetInt(args, "mes", DateTime.Today.Month, 1, 12);
        var occurrences = await _absences.ListAsync(year, month, military.Id);
        var rows = occurrences.OrderByDescending(x => x.Date).Select(x => new
        {
            data = x.DateText,
            horario = x.Time,
            tipo = x.Type,
            minutos = x.Minutes,
            justificada = x.Justified,
            motivo = x.Reason,
            medida = x.Measure,
            observacao = TrimText(x.Notes, 700)
        }).ToList();
        return Result(new
        {
            success = true,
            militar = $"{military.ShortRank} {military.Name}",
            competencia = $"{month:00}/{year}",
            quantidade = rows.Count,
            ocorrencias = rows
        }, $"Faltas/atrasos de {military.WarName}: {rows.Count}");
    }

    private async Task<AssistantToolExecutionResult> GetDutyRosterAsync(JsonObject args, CancellationToken ct)
    {
        var military = await ResolveMilitaryAsync(GetString(args, "militar"), ct);
        if (military is null) return Result(new { success = false, error = "Militar não localizado ou consulta ambígua." }, "Escala: militar não localizado");
        var year = GetInt(args, "ano", DateTime.Today.Year, 2000, 2100);
        var month = GetInt(args, "mes", DateTime.Today.Month, 1, 12);
        var store = await _dutyRoster.LoadAsync() ?? new DutyRosterStore();
        var monthKey = $"{year:0000}-{month:00}";
        if (!store.Months.TryGetValue(monthKey, out var rosterMonth))
            return Result(new { success = true, militar = $"{military.ShortRank} {military.Name}", competencia = $"{month:00}/{year}", quantidade = 0, servicos = Array.Empty<object>(), marcacoes = Array.Empty<object>() }, $"Escala de {military.WarName}: sem dados");

        var personKey = $"M:{military.Id}";
        var services = rosterMonth.Assignments
            .Where(x => x.Value.Equals(personKey, StringComparison.OrdinalIgnoreCase))
            .Select(x => ParseRosterDate(x.Key))
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .OrderBy(x => x)
            .Select(x => new { data = x.ToString("dd/MM/yyyy"), dia_semana = x.ToString("dddd", CultureInfo.GetCultureInfo("pt-BR")), dia_vermelho = rosterMonth.RedDays.Contains(x.Day) })
            .ToList();

        var marks = rosterMonth.Marks
            .Where(x => x.Key.StartsWith(personKey + "|", StringComparison.OrdinalIgnoreCase))
            .Select(x => new { data_raw = x.Key[(personKey.Length + 1)..], tipo = x.Value })
            .Select(x => new { data = ParseRosterDate(x.data_raw)?.ToString("dd/MM/yyyy") ?? x.data_raw, tipo = x.tipo })
            .OrderBy(x => x.data)
            .ToList();

        return Result(new
        {
            success = true,
            militar = $"{military.ShortRank} {military.Name}",
            competencia = $"{month:00}/{year}",
            quantidade = services.Count,
            servicos = services,
            marcacoes = marks
        }, $"Escala de {military.WarName}: {services.Count} serviço(s)");
    }


    private async Task<AssistantToolExecutionResult> SearchAllBulletinsToolAsync(JsonObject args, AssistantSettings settings, CancellationToken ct)
    {
        var reference = GetString(args, "militar");
        var subject = GetString(args, "assunto");
        var limit = GetInt(args, "limite", 30, 1, 80);
        MilitaryRecord? military = null;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            try { military = await ResolveMilitaryAsync(reference, ct); } catch { military = null; }
        }
        var rows = await SearchAllBulletinSourcesAsync(military, reference, subject, limit, ct);
        var actions = BuildOpenActionsFromBulletinRows(rows, 12);
        return Result(new
        {
            success = true,
            militar_resolvido = military is null ? null : new { id = military.Id, posto_graduacao = military.Rank, nome = military.Name, nome_guerra = military.WarName },
            consulta = new { militar = reference, assunto = subject },
            quantidade = rows.Count,
            resultados = rows,
            aviso_links = actions.Count == 0 ? "Nenhum PDF local foi encontrado para os resultados." : "Foram gerados links seguros para abrir os PDFs somente mediante clique do operador."
        }, $"Pesquisa ampliada em boletins/aditamentos: {rows.Count} resultado(s)", actions);
    }

    private async Task<AssistantToolExecutionResult> RunPaymentConferenceForAssistantAsync(JsonObject args, CancellationToken ct)
    {
        var settings = await _paymentConference.LoadSettingsAsync(ct);
        var year = GetInt(args, "ano", 0, 0, 2100);
        var month = GetInt(args, "mes", 0, 0, 12);
        if (year > 0) settings.Year = year;
        if (month > 0) settings.Month = month;
        var limit = GetInt(args, "limite_linhas", 40, 1, 200);
        var saveReport = GetBool(args, "salvar_relatorio");
        var bulletins = await _paymentConference.LoadFurrielBulletinsAsync(ct);
        var selected = bulletins
            .Where(x => !string.IsNullOrWhiteSpace(x.Path) && File.Exists(x.Path))
            .OrderByDescending(x => ParseDate(x.Date) ?? DateTime.MinValue)
            .Take(12)
            .Select(x => x.Path)
            .ToList();
        if (selected.Count == 0)
            return Result(new { success = false, error = "Nenhum aditamento do Furriel indexado foi encontrado para conferência." }, "Conferência de pagamento: sem aditamentos");

        var result = await _paymentConference.RunAsync(selected, settings, null, ct);
        string csv = string.Empty;
        if (saveReport) csv = await _paymentConference.ExportCsvAsync(result, ct);
        var rows = result.Rows
            .OrderBy(x => x.Status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(x => x.Status, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Military, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(x => new
            {
                status = x.Status,
                gravidade = x.Severity,
                militar = x.Military,
                cpf = MilitaryFormatting.FormatCpf(x.Cpf),
                prec_cp = x.PrecCp,
                boletim = x.Bulletin,
                data_boletim = x.BulletinDate,
                pagina = x.BulletinPage,
                direito = x.PaymentType,
                modo = x.PaymentMode,
                valor_publicado = x.ExpectedAmountText,
                valor_recebido = x.PaidAmountText,
                diferenca = x.DifferenceText,
                rubricas = x.RubricsFound,
                contracheque = string.IsNullOrWhiteSpace(x.PaystubPath) ? string.Empty : Path.GetFileName(x.PaystubPath),
                observacao = TrimText(x.Notes, 700)
            }).ToList();
        return Result(new
        {
            success = true,
            competencia = $"{settings.Month:00}/{settings.Year}",
            aditamentos_lidos = selected.Count,
            resumo = result.Summary,
            avisos = result.Warnings,
            linhas_retornadas = rows.Count,
            pendencias_e_resultados = rows,
            relatorio_csv = string.IsNullOrWhiteSpace(csv) ? null : csv
        }, $"Conferência de pagamento {settings.Month:00}/{settings.Year}: {result.Summary.Ok} OK, {result.Summary.MissingRubric} não recebeu, {result.Summary.Divergent} divergente, {result.Summary.MissingPaystub} sem contracheque");
    }

    private async Task<AssistantToolExecutionResult> CreateOperationalReminderAsync(JsonObject args, CancellationToken ct)
    {
        var title = GetString(args, "titulo");
        var date = GetString(args, "data");
        var priority = GetString(args, "prioridade");
        var description = GetString(args, "descricao");
        if (string.IsNullOrWhiteSpace(title)) title = "Lembrete operacional";
        if (!ReminderRules.Priorities.Any(x => x.Equals(priority, StringComparison.OrdinalIgnoreCase))) priority = "Normal";
        var parsed = ReminderDate.Parse(date);
        var record = new ReminderRecord
        {
            Title = title.Trim(),
            Date = parsed?.ToString("dd/MM/yyyy") ?? string.Empty,
            Body = description.Trim(),
            Priority = ReminderRules.Priorities.FirstOrDefault(x => x.Equals(priority, StringComparison.OrdinalIgnoreCase)) ?? "Normal",
            Recurrence = "Nenhuma",
            AutoReschedule = true,
            Completed = false
        };
        await _reminders.SaveAsync(record, ct);
        return Result(new
        {
            success = true,
            id = record.Id,
            titulo = record.Title,
            data = record.FormattedDate,
            prazo = record.DaysText,
            prioridade = record.EffectivePriority,
            descricao = record.Body
        }, $"Lembrete criado: {record.Title}");
    }


    private static List<AssistantPendingAction> BuildOpenActionsFromBulletinRows(IReadOnlyList<Dictionary<string, object?>> rows, int limit)
    {
        var actions = new List<AssistantPendingAction>();
        foreach (var row in rows)
        {
            if (actions.Count >= Math.Max(1, limit)) break;
            var path = Value(row, "caminho");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

            var label = BuildBulletinRowLinkLabel(row);
            var fileName = Value(row, "arquivo");
            var page = Value(row, "pagina");
            var description = "Abrir PDF salvo";
            if (!string.IsNullOrWhiteSpace(fileName)) description += $": {fileName}";
            if (!string.IsNullOrWhiteSpace(page) && page != "0" && page != "—") description += $" • página {page}";

            var action = AssistantActionRegistry.OpenFile(label, path, description);
            action.Payload["display"] = label;
            actions.Add(action);
        }

        return actions
            .GroupBy(x => string.Join("|", x.Type, x.ConversationLinkLabel, string.Join(";", x.FilePaths)), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private static string BuildBulletinRowLinkLabel(IReadOnlyDictionary<string, object?> row)
    {
        var parts = new List<string>();
        var source = Value(row, "fonte");
        var bulletin = Value(row, "boletim");
        var date = Value(row, "data");
        var type = Value(row, "tipo");
        var page = Value(row, "pagina");

        if (!string.IsNullOrWhiteSpace(bulletin) && bulletin != "—")
        {
            if (source.Contains("Furriel", StringComparison.OrdinalIgnoreCase) && !bulletin.StartsWith("ADT", StringComparison.OrdinalIgnoreCase))
                parts.Add("ADT " + bulletin);
            else if (!bulletin.StartsWith("BI", StringComparison.OrdinalIgnoreCase) && source.Contains("Boletim", StringComparison.OrdinalIgnoreCase))
                parts.Add("BI " + bulletin);
            else
                parts.Add(bulletin);
        }

        if (!string.IsNullOrWhiteSpace(date) && date != "—") parts.Add(date);
        if (!string.IsNullOrWhiteSpace(type) && type != "—") parts.Add(type);
        if (!string.IsNullOrWhiteSpace(page) && page != "0" && page != "—") parts.Add("pág. " + page);
        return parts.Count == 0 ? "Abrir PDF vinculado" : string.Join(" — ", parts);
    }

    private static string Value(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty : string.Empty;

    private async Task<List<Dictionary<string, object?>>> SearchAllBulletinSourcesAsync(MilitaryRecord? military, string person, string subject, int limit, CancellationToken ct)
    {
        var results = new List<Dictionary<string, object?>>();
        var personAliases = BuildPersonAliases(military, person);
        var subjectTerms = Normalize(subject).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();

        bool Match(string text)
        {
            var norm = Normalize(text);
            var personOk = personAliases.Count == 0 || personAliases.Any(alias => AliasMatches(norm, alias));
            var subjectOk = subjectTerms.Length == 0 || subjectTerms.All(term => norm.Contains(term, StringComparison.Ordinal));
            return personOk && subjectOk;
        }

        void Add(string fonte, string boletim, string data, int pagina, string tipo, string arquivo, string contexto, string caminho = "")
        {
            if (results.Count >= limit) return;
            var key = Normalize($"{fonte} {boletim} {data} {pagina} {tipo} {arquivo} {contexto}");
            if (results.Any(x => string.Equals(x.TryGetValue("_key", out var k) ? k?.ToString() : string.Empty, key, StringComparison.Ordinal))) return;
            results.Add(new Dictionary<string, object?>
            {
                ["fonte"] = fonte,
                ["boletim"] = boletim,
                ["data"] = data,
                ["pagina"] = pagina,
                ["tipo"] = tipo,
                ["arquivo"] = arquivo,
                ["contexto"] = TrimText(contexto, 1400),
                ["caminho"] = caminho,
                ["_key"] = key
            });
        }

        try
        {
            var store = await _bulletins.LoadAsync(ct);
            foreach (var file in store.Items.OrderByDescending(x => ParseDate(x.DateIso) ?? DateTime.MinValue))
            {
                foreach (var finding in file.Findings)
                {
                    var text = string.Join(" ", finding.Military, finding.FullName, finding.WarName, finding.Rank, finding.Category, finding.Type, finding.Detail, finding.Context);
                    if (!Match(text)) continue;
                    Add("Boletim Inteligente", finding.Bulletin == "—" ? file.BulletinNumber : finding.Bulletin, finding.BulletinDate == "—" ? file.BulletinDate : finding.BulletinDate, finding.Page, string.Join(" / ", finding.Category, finding.Type).Trim(' ', '/'), file.FileName, string.Join(" — ", new[] { finding.DisplayMilitary, finding.Detail, finding.Context }.Where(x => !string.IsNullOrWhiteSpace(x))), file.PdfPath);
                    if (results.Count >= limit) break;
                }
                if (results.Count >= limit) break;
                if (file.Findings.Count == 0 || subjectTerms.Length > 0)
                {
                    var text = await _bulletins.ReadCachedTextAsync(file, ct);
                    if (Match(text)) Add("Boletim Inteligente - texto", file.BulletinNumber, file.BulletinDate, 0, "Texto integral", file.FileName, TrimText(text, 1400), file.PdfPath);
                }
                if (results.Count >= limit) break;
            }
        }
        catch { }

        try
        {
            if (File.Exists(_paths.FurrielIndexFile))
            {
                var json = await File.ReadAllTextAsync(_paths.FurrielIndexFile, ct);
                var store = JsonSerializer.Deserialize<FurrielIndexStore>(json) ?? new FurrielIndexStore();
                foreach (var file in store.Files.OrderByDescending(x => ParseDate(x.Date) ?? DateTime.MinValue))
                {
                    foreach (var line in file.Lines)
                    {
                        var text = string.Join(" ", line.Text, line.Subject, line.Major, file.Title, file.Bulletin, file.Date);
                        if (!Match(text)) continue;
                        Add("Aditamento do Furriel", file.Bulletin, file.Date, line.Page, line.Subject, string.IsNullOrWhiteSpace(file.OriginalName) ? Path.GetFileName(file.StoredPath) : file.OriginalName, line.Text, file.StoredPath);
                        if (results.Count >= limit) break;
                    }
                    if (results.Count >= limit) break;
                }
            }
        }
        catch { }

        try
        {
            if (File.Exists(_paths.ExternalBulletinIndexFile))
            {
                var json = await File.ReadAllTextAsync(_paths.ExternalBulletinIndexFile, ct);
                var store = JsonSerializer.Deserialize<ExternalBulletinStore>(json) ?? new ExternalBulletinStore();
                foreach (var file in store.Items.OrderByDescending(x => ParseDate(x.DateIso) ?? DateTime.MinValue))
                {
                    foreach (var mention in file.Mentions)
                    {
                        var text = string.Join(" ", mention.Section, mention.Type, mention.Summary, mention.MatchLine, mention.Context, mention.Event, mention.Personnel, file.BulletinType, file.BulletinNumber, file.BulletinDate);
                        if (!Match(text)) continue;
                        Add(ExternalBulletinKinds.DisplayName(file.Kind), string.IsNullOrWhiteSpace(mention.Bulletin) ? file.BulletinNumber : mention.Bulletin, string.IsNullOrWhiteSpace(mention.BulletinDate) ? file.BulletinDate : mention.BulletinDate, mention.Page, mention.Type, file.OriginalFileName, string.Join(" — ", new[] { mention.Section, mention.Summary, mention.MatchLine, mention.Context }.Where(x => !string.IsNullOrWhiteSpace(x))), file.StoredPath);
                        if (results.Count >= limit) break;
                    }
                    if (results.Count >= limit) break;
                }
            }
        }
        catch { }

        foreach (var row in results) row.Remove("_key");
        return results;
    }

    private static List<string> BuildPersonAliases(MilitaryRecord? military, string person)
    {
        var aliases = new List<string>();
        void Add(string? value)
        {
            var norm = Normalize(value);
            if (!string.IsNullOrWhiteSpace(norm) && !aliases.Contains(norm, StringComparer.Ordinal)) aliases.Add(norm);
        }
        Add(person);
        if (military is not null)
        {
            Add(military.Name);
            Add(military.WarName);
            Add(military.Cpf);
            Add(military.FormattedCpf);
            Add(military.PrecCp);
            Add(military.MilitaryId);
            foreach (var part in Normalize(military.Name).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (part.Length >= 4) Add(part);
        }
        return aliases;
    }

    private static bool AliasMatches(string normalizedText, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return false;
        if (alias.Length <= 3) return normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(alias, StringComparer.Ordinal);
        var parts = alias.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts.All(p => normalizedText.Contains(p, StringComparison.Ordinal))) return true;
        return normalizedText.Contains(alias, StringComparison.Ordinal);
    }

    private Task<AssistantToolExecutionResult> FindGeneratedFilesAsync(JsonObject args, CancellationToken ct)
    {
        var query = GetString(args, "consulta");
        var limit = GetInt(args, "limite", 15, 1, 30);
        var normalized = Normalize(query);
        Directory.CreateDirectory(_paths.GeneratedDocumentsDirectory);
        var files = Directory.EnumerateFiles(_paths.GeneratedDocumentsDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => string.IsNullOrWhiteSpace(normalized) || Normalize(file.Name + " " + file.DirectoryName).Contains(normalized, StringComparison.Ordinal))
            .OrderByDescending(file => file.LastWriteTime)
            .Take(limit)
            .Select(file => new
            {
                nome = file.Name,
                pasta = Path.GetRelativePath(_paths.GeneratedDocumentsDirectory, file.DirectoryName ?? _paths.GeneratedDocumentsDirectory),
                alterado_em = file.LastWriteTime.ToString("dd/MM/yyyy HH:mm"),
                tamanho_bytes = file.Length
            }).ToList();
        return Task.FromResult(Result(new { success = true, consulta = query, quantidade = files.Count, arquivos = files }, $"Arquivos gerados: {files.Count} resultado(s)"));
    }

    private Task<AssistantToolExecutionResult> RequestPrintAsync(JsonObject args, CancellationToken ct)
    {
        var reference = GetString(args, "arquivo_ou_consulta");
        var copies = GetInt(args, "copias", 1, 1, 20);
        var files = ResolveGeneratedFiles(reference, 12);
        if (files.Count == 0)
            return Task.FromResult(Result(new { success = false, error = "Nenhum arquivo gerado foi localizado para impressão." }, "Impressão: arquivo não localizado"));

        var action = new AssistantPendingAction
        {
            Type = "print",
            Title = files.Count == 1 ? "Preparar impressão" : $"Preparar {files.Count} arquivos para impressão",
            Description = $"Abrir a fila de impressão do SIGFUR com {copies} cópia(s). A impressão somente será iniciada após sua conferência.",
            FilePaths = files,
            Copies = copies
        };
        var result = Result(new
        {
            success = true,
            status = "aguardando_confirmacao_do_operador",
            quantidade = files.Count,
            copias = copies,
            arquivos = files.Select(Path.GetFileName).ToList()
        }, "Impressão preparada para confirmação");
        result.PendingActions.Add(action);
        return Task.FromResult(result);
    }

    private List<string> ResolveGeneratedFiles(string reference, int limit)
    {
        Directory.CreateDirectory(_paths.GeneratedDocumentsDirectory);
        if (!string.IsNullOrWhiteSpace(reference))
        {
            try
            {
                var full = Path.GetFullPath(reference);
                var root = Path.GetFullPath(_paths.GeneratedDocumentsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return [full];
            }
            catch { }
        }
        var normalized = Normalize(reference);
        return Directory.EnumerateFiles(_paths.GeneratedDocumentsDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => string.IsNullOrWhiteSpace(normalized) || Normalize(file.Name + " " + file.DirectoryName).Contains(normalized, StringComparison.Ordinal))
            .OrderByDescending(file => file.LastWriteTime)
            .Take(limit)
            .Select(x => x.FullName)
            .ToList();
    }

    private async Task<MilitaryRecord?> ResolveMilitaryAsync(string reference, CancellationToken ct)
    {
        var rows = await _military.GetAllAsync(ct);
        if (int.TryParse(reference, out var id)) return rows.FirstOrDefault(x => x.Id == id);
        var normalized = CleanMilitaryReference(reference);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var exact = rows.Where(x => Normalize(x.Name) == normalized || Normalize(x.WarName) == normalized).ToList();
        if (exact.Count == 1) return exact[0];
        var ranked = rows.Select(x => new { Item = x, Score = ScoreMilitary(x, normalized) }).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
        if (ranked.Count == 0) return null;
        if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score) return null;
        return ranked[0].Item;
    }

    private static int ScoreMilitary(MilitaryRecord item, string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return 0;
        var name = Normalize(item.Name);
        var war = Normalize(item.WarName);
        var searchable = Normalize(string.Join(" ", item.Rank, item.ShortRank, item.Name, item.WarName, item.Cpf, item.PrecCp, item.MilitaryId));
        if (name == normalized || war == normalized || Normalize(item.Cpf) == normalized || Normalize(item.PrecCp) == normalized || Normalize(item.MilitaryId) == normalized) return 100;
        if (name.StartsWith(normalized, StringComparison.Ordinal) || war.StartsWith(normalized, StringComparison.Ordinal)) return 80;
        if (name.Contains(normalized, StringComparison.Ordinal) || war.Contains(normalized, StringComparison.Ordinal)) return 65;
        var terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = terms.Count(term => searchable.Contains(term, StringComparison.Ordinal));
        return found == terms.Length ? 40 + found : 0;
    }

    private static bool MatchesBulletinFinding(IntelligentBulletinFinding finding, string person, string subject)
    {
        var personText = Normalize(string.Join(" ", finding.Military, finding.FullName, finding.WarName, finding.Rank));
        var subjectText = Normalize(string.Join(" ", finding.Category, finding.Type, finding.Detail, finding.Context));
        var personOk = string.IsNullOrWhiteSpace(person) || person.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(x => personText.Contains(x, StringComparison.Ordinal));
        var subjectOk = string.IsNullOrWhiteSpace(subject) || subject.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(x => subjectText.Contains(x, StringComparison.Ordinal));
        return personOk && subjectOk;
    }

    private static string FindSnippet(string text, string person, string subject, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = Normalize(text);
        var terms = new[] { person, subject }.Where(x => !string.IsNullOrWhiteSpace(x)).SelectMany(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct().ToList();
        if (terms.Count == 0 || terms.Any(term => !normalized.Contains(term, StringComparison.Ordinal))) return string.Empty;
        var first = terms.Select(term => normalized.IndexOf(term, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Clamp(first - maxLength / 3, 0, Math.Max(0, text.Length - 1));
        var length = Math.Min(maxLength, text.Length - start);
        return TrimText(text.Substring(start, length), maxLength);
    }

    private static AssistantToolExecutionResult Result(object value, string summary, List<AssistantPendingAction>? pendingActions = null)
        => new() { OutputJson = JsonSerializer.Serialize(value), Summary = summary, PendingActions = pendingActions ?? [] };

    private static bool ShouldRevealSensitiveField(JsonObject args, AssistantSettings settings, string field)
        => !settings.RedactSensitiveData || RequestedSensitiveFields(args).Contains(field);

    private static bool HasRequestedSensitiveFields(JsonObject args)
        => RequestedSensitiveFields(args).Count > 0;

    private static HashSet<string> RequestedSensitiveFields(JsonObject args)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (args["campos_solicitados"] is not JsonArray array) return fields;
        foreach (var item in array)
        {
            try
            {
                var value = item?.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(value)) fields.Add(value);
            }
            catch { }
        }
        return fields;
    }

    private static string GetString(JsonObject args, string key)
        => args[key]?.GetValue<string>()?.Trim() ?? string.Empty;

    private static bool GetBool(JsonObject args, string key, bool fallback = false)
    {
        try { return args[key]?.GetValue<bool>() ?? fallback; }
        catch { return fallback; }
    }

    private static int GetInt(JsonObject args, string key, int fallback, int min, int max)
    {
        try
        {
            var value = args[key]?.GetValue<int>() ?? fallback;
            return Math.Clamp(value, min, max);
        }
        catch { return Math.Clamp(fallback, min, max); }
    }

    private static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : ' ');
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static string CleanMilitaryReference(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        foreach (var word in AssistantSearchStopWords.OrderByDescending(x => x.Length))
            normalized = Regex.Replace(normalized, $"(^|\\s){Regex.Escape(word)}($|\\s)", " ", RegexOptions.CultureInvariant);

        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static readonly HashSet<string> AssistantSearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "QUERIA", "QUERO", "SABER", "QUAL", "QUAIS", "NUMERO", "NÚMERO", "BOLETIM", "BI", "ADT", "ADITAMENTO",
        "NOTA", "ASSUNTO", "SOBRE", "TEM", "EXISTE", "ESTA", "ESTÁ", "INCLUSAO", "INCLUSÃO", "INCLUIDO", "INCLUÍDO",
        "INCLUIDA", "INCLUÍDA", "PUBLICACAO", "PUBLICAÇÃO", "FERIAS", "FÉRIAS", "AUXILIO", "AUXÍLIO", "TRANSPORTE",
        "DESPESA", "ANULAR", "GRATIFICACAO", "GRATIFICAÇÃO", "REPRESENTACAO", "REPRESENTAÇÃO", "CONTRACHEQUE",
        "CARTEIRA", "PASTA", "MILITAR", "SGT", "SARGENTO", "SOLDADO", "CABO", "TENENTE", "SUBTENENTE", "SUB", "TEN", "ST",
        "DO", "DA", "DE", "DOS", "DAS", "O", "A", "OS", "AS", "EM", "NO", "NA", "NOS", "NAS", "PARA", "PRA", "QUE"
    };

    private static string TrimText(string? value, int max)
    {
        var text = Regex.Replace((value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static DateTime? ParseRosterDate(string? value)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return exact.Date;
        return DateTime.TryParse(value, out var parsed) ? parsed.Date : null;
    }

    private static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var parsed) ? parsed : null;
    private static int BulletinNumber(string? value) => int.TryParse(Regex.Match(value ?? string.Empty, @"\d+").Value, out var number) ? number : 0;
}
