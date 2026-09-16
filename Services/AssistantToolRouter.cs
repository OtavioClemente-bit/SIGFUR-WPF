namespace SIGFUR.Wpf.Services;

/// <summary>
/// Classifica cada solicitação antes de chamar o modelo. O objetivo é manter
/// toda a capacidade do SIGFUR sem reenviar o catálogo completo de ferramentas em
/// perguntas que precisam de apenas um domínio — ou de nenhuma ferramenta.
/// </summary>
public sealed class AssistantToolRoute
{
    public string DomainLabel { get; init; } = "Conversa geral";
    public IReadOnlySet<string> ToolNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public bool UseDeterministicEvidence { get; init; }
    public bool IncludeBulletinContext { get; init; }
    public bool IncludePaymentContext { get; init; }
    public int MaxToolRounds { get; init; } = 1;
    public int MaxHistoryMessages { get; init; } = 4;
    public int RecommendedMaxOutputTokens { get; init; } = 700;
    public string RecommendedReasoningEffort { get; init; } = "low";
    public bool AllowOfficialWebResearch { get; init; }
    public string PromptCacheKey { get; init; } = "sigfur-assistant-general-v2";
    public bool HasTools => ToolNames.Count > 0;
}

public static class AssistantToolRouter
{
    public static AssistantToolRoute Route(string prompt, bool hasAttachments = false)
    {
        var normalized = AssistantIntentDetector.Normalize(prompt);
        var intent = AssistantIntentDetector.Detect(prompt);
        var tools = new List<string>();
        var domains = new List<string>();

        void AddDomain(string name)
        {
            if (!domains.Contains(name, StringComparer.OrdinalIgnoreCase)) domains.Add(name);
        }

        void AddTools(params string[] names)
        {
            foreach (var name in names)
                if (!tools.Contains(name, StringComparer.Ordinal)) tools.Add(name);
        }

        var legislation = intent.Kind == AssistantIntentKind.LegislationResearch || AssistantIntentDetector.ContainsAny(normalized,
            "legislacao", "lei", "decreto", "portaria", "norma", "artigo", "inciso", "amparo legal", "base legal", "fundamento legal", "faz jus", "direito",
            "regulamento", "estatuto", "instrucao normativa", "prazo legal", "requisito legal", "licenca", "afastamento", "indenizacao", "beneficio");
        var explicitBulletin = AssistantIntentDetector.ContainsAny(normalized,
            "boletim", "bi", "adt", "aditamento", "publicacao", "nota para boletim", "sippes");
        var bulletin = intent.Kind == AssistantIntentKind.OpenBulletin || explicitBulletin;
        var draftingBulletin = bulletin && AssistantIntentDetector.ContainsAny(normalized,
            "crie", "criar", "redija", "redigir", "escreva", "montar", "gere", "gerar", "minuta", "corpo de boletim", "melhore");
        var vacation = intent.Kind == AssistantIntentKind.VacationSearch || AssistantIntentDetector.ContainsAny(normalized,
            "ferias", "adicional de ferias", "plano de ferias");
        var transport = intent.Kind == AssistantIntentKind.TransportAidConference || AssistantIntentDetector.ContainsAny(normalized,
            "auxilio transporte", "aux transporte", "despesa a anular", "vale transporte");
        var gratification = intent.Kind == AssistantIntentKind.GratificationSearch || AssistantIntentDetector.ContainsAny(normalized,
            "gratificacao de representacao", "gratificacao", "adicional de habilitacao", "adicional militar");
        var paystub = intent.Kind is AssistantIntentKind.OpenPaystub or AssistantIntentKind.PrintPaystub
                      || AssistantIntentDetector.ContainsAny(normalized, "contracheque", "contra cheque", "ficha financeira");
        var payment = AssistantIntentDetector.ContainsAny(normalized,
            "corrida de pagamento", "nao recebeu", "quem recebeu", "valor divergente", "conferencia de pagamento", "conferir pagamento", "conferir rubrica", "rubrica divergente");
        var reminders = AssistantIntentDetector.ContainsAny(normalized, "lembrete", "lembretes", "me lembre", "lembrar", "nao esquecer", "cobrar");
        var absences = AssistantIntentDetector.ContainsAny(normalized, "falta", "faltas", "atraso", "atrasos", "ausencia", "ausencias");
        var duty = AssistantIntentDetector.ContainsAny(normalized, "escala", "sgt de dia", "dia de servico");
        var generatedFiles = intent.Kind == AssistantIntentKind.GeneratedFiles || AssistantIntentDetector.ContainsAny(normalized,
            "arquivo gerado", "documento gerado", "relatorio gerado", "localizar arquivo", "imprimir arquivo");
        var personnel = intent.Kind is AssistantIntentKind.SearchPerson or AssistantIntentKind.PersonFullSummary or AssistantIntentKind.OpenWallet or AssistantIntentKind.OpenFolder or AssistantIntentKind.Route
                        || AssistantIntentDetector.ContainsAny(normalized,
                            "militar", "nome de guerra", "cpf", "prec cp", "preccp", "identidade", "dados cadastrais", "carteira do", "documentos do militar", "efetivo");

        // Um anexo deve ser analisado diretamente pelo modelo. Só adicionamos uma base
        // do SIGFUR quando a própria pergunta pedir explicitamente o cruzamento.
        if (hasAttachments && !legislation && !bulletin && !personnel && !payment)
            return new AssistantToolRoute
            {
                DomainLabel = "Análise de documento anexado",
                MaxHistoryMessages = 2,
                RecommendedMaxOutputTokens = 1_200,
                RecommendedReasoningEffort = "low",
                PromptCacheKey = "sigfur-document-review-v2"
            };

        if (legislation)
        {
            AddDomain("Legislação");
            AddTools("pesquisar_legislacao_local");
        }

        if (bulletin)
        {
            AddDomain(draftingBulletin ? "Redação de boletim" : "Boletins e aditamentos");
            if (draftingBulletin)
                AddTools("consultar_regras_boletim", "gerar_corpo_boletim", "pesquisar_legislacao_local");
            else
                AddTools("pesquisar_todos_boletins", "pesquisar_boletins");
        }

        if (vacation)
        {
            AddDomain("Férias");
            AddTools("consultar_ferias", "consultar_alertas_corrida_pagamento");
        }

        if (transport)
        {
            AddDomain("Auxílio-transporte");
            AddTools("consultar_auxilio_transporte", "pesquisar_legislacao_local");
        }

        if (gratification)
        {
            AddDomain("Gratificações e adicionais");
            AddTools("pesquisar_todos_boletins", "pesquisar_legislacao_local");
        }

        if (paystub)
        {
            AddDomain("Contracheques");
            AddTools("consultar_contracheques");
        }

        if (payment)
        {
            AddDomain("Pagamento");
            AddTools("conferir_pagamento_ia", "pesquisar_todos_boletins", "consultar_contracheques",
                "consultar_alertas_corrida_pagamento", "pesquisar_legislacao_local");
        }

        if (reminders)
        {
            AddDomain("Lembretes");
            AddTools(AssistantIntentDetector.ContainsAny(normalized, "me lembre", "crie", "criar", "lembrar", "nao esquecer")
                ? "criar_lembrete_operacional"
                : "consultar_lembretes");
        }

        if (absences)
        {
            AddDomain("Faltas e atrasos");
            AddTools("consultar_faltas_atrasos");
        }

        if (duty)
        {
            AddDomain("Escala de serviço");
            AddTools("consultar_escala_servico");
        }

        if (generatedFiles)
        {
            AddDomain("Documentos gerados");
            AddTools("localizar_arquivos_gerados");
            if (intent.WantsPrint) AddTools("solicitar_impressao");
        }

        if (personnel)
        {
            AddDomain("Pessoal");
            if (AssistantIntentDetector.ContainsAny(normalized, "quantos militares", "efetivo", "quantidade de militares"))
                AddTools("consultar_efetivo");
            else if (AssistantIntentDetector.ContainsAny(normalized, "licenciado", "licenciados", "transferido", "transferidos"))
                AddTools("buscar_licenciados_transferidos");
            else if (AssistantIntentDetector.ContainsAny(normalized, "documento", "documentos", "carteira", "pasta"))
                AddTools("buscar_militar", "consultar_documentos_militar");
            else
                AddTools("buscar_militar", "consulta_operacional_militar");
        }

        var directEvidence = intent.Kind == AssistantIntentKind.LegislationResearch
            || (bulletin && intent.Kind is AssistantIntentKind.SearchBulletins or AssistantIntentKind.OpenBulletin)
            || intent.Kind is AssistantIntentKind.OpenPaystub or AssistantIntentKind.PrintPaystub
                or AssistantIntentKind.OpenWallet or AssistantIntentKind.OpenFolder or AssistantIntentKind.Route
                or AssistantIntentKind.GeneratedFiles;

        // O limite mantém o esquema de ferramentas pequeno mesmo em perguntas compostas.
        // As primeiras ferramentas refletem a ordem dos domínios detectados.
        var selected = tools.Take(6).ToHashSet(StringComparer.Ordinal);
        var complex = selected.Count > 0 || hasAttachments;
        var complexPayment = payment && (AssistantIntentDetector.ContainsAny(normalized,
            "divergencia", "divergente", "ambigu", "multiplas publicacoes", "revisao necessaria"));
        var allowOfficialResearch = legislation && AssistantIntentDetector.ContainsAny(normalized,
            "pesquise", "pesquisar", "internet", "atual", "vigencia", "oficial");
        var cacheKey = payment ? "sigfur-payment-monthly-v2"
            : bulletin ? "sigfur-bulletin-audit-v2"
            : vacation ? "sigfur-vacation-v2"
            : legislation ? "sigfur-legislation-v2"
            : "sigfur-assistant-general-v2";
        return new AssistantToolRoute
        {
            DomainLabel = domains.Count == 0 ? "Conversa e redação geral" : string.Join(" + ", domains),
            ToolNames = selected,
            UseDeterministicEvidence = directEvidence,
            IncludeBulletinContext = bulletin,
            IncludePaymentContext = payment || vacation,
            MaxToolRounds = selected.Count == 0 ? 1 : 3,
            MaxHistoryMessages = complex ? 4 : 6,
            RecommendedMaxOutputTokens = draftingBulletin || legislation || hasAttachments ? 1_200 : complex ? 900 : 700,
            RecommendedReasoningEffort = complexPayment ? "medium" : "low",
            AllowOfficialWebResearch = allowOfficialResearch,
            PromptCacheKey = cacheKey
        };
    }
}
