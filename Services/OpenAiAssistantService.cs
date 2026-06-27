using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class OpenAiAssistantService
{
    private readonly HttpClient _httpClient;
    private readonly AssistantCredentialService _credentials;
    private readonly AssistantStorageService _storage;
    private readonly AssistantDataService _data;
    private readonly SettingsService _settings;
    private readonly BulletinKnowledgeService _bulletinKnowledge;
    private readonly LogService _log;

    public OpenAiAssistantService(
        AssistantCredentialService credentials,
        AssistantStorageService storage,
        AssistantDataService data,
        SettingsService settings,
        BulletinKnowledgeService bulletinKnowledge,
        LogService log)
    {
        _credentials = credentials;
        _storage = storage;
        _data = data;
        _settings = settings;
        _bulletinKnowledge = bulletinKnowledge;
        _log = log;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    public async Task<AssistantApiResult> SendAsync(
        IReadOnlyList<AssistantConversationMessage> history,
        string userPrompt,
        IReadOnlyList<AssistantAttachmentItem> attachments,
        AssistantSettings assistantSettings,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _credentials.ReadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("A chave da OpenAI ainda não foi configurada. Abra Configurar API no Assistente SIGFUR.");

        var usageBefore = await _storage.GetCurrentMonthUsageAsync(assistantSettings);
        if (assistantSettings.HardBudgetLimit && assistantSettings.MonthlyBudgetBrl > 0 && usageBefore.EstimatedCostBrl >= assistantSettings.MonthlyBudgetBrl)
            throw new InvalidOperationException($"O limite interno mensal de {assistantSettings.MonthlyBudgetBrl:C2} foi atingido. Ajuste o limite nas configurações para continuar.");

        var profile = await _settings.LoadProfileAsync();
        var bulletinContext = await _bulletinKnowledge.BuildAssistantContextAsync(userPrompt, cancellationToken);
        var paymentRunContext = assistantSettings.EnableLocalDataTools
            ? await _data.BuildPaymentRunAlertContextAsync(cancellationToken)
            : string.Empty;

        var totalInput = 0;
        var totalOutput = 0;
        var webResearchContext = string.Empty;
        if (ShouldUseInternetResearch(userPrompt, attachments))
        {
            var web = await TryBuildInternetResearchContextAsync(userPrompt, assistantSettings, apiKey, cancellationToken);
            webResearchContext = web.Context;
            totalInput += web.InputTokens;
            totalOutput += web.OutputTokens;
        }

        var messages = BuildMessages(history, userPrompt, attachments, assistantSettings, profile, bulletinContext, paymentRunContext, webResearchContext);
        var tools = assistantSettings.EnableLocalDataTools ? _data.BuildToolDefinitions() : new JsonArray();
        var pendingActions = new List<AssistantPendingAction>();
        var toolSummaries = new List<string>();
        if (!string.IsNullOrWhiteSpace(webResearchContext)) toolSummaries.Add("Internet: pesquisa com fontes oficiais");
        string finalText = string.Empty;

        for (var round = 0; round < 6; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new JsonObject
            {
                ["model"] = assistantSettings.Model,
                ["messages"] = messages.DeepClone(),
                ["max_completion_tokens"] = assistantSettings.MaxOutputTokens
            };
            if (tools.Count > 0)
            {
                request["tools"] = tools.DeepClone();
                request["tool_choice"] = "auto";
                request["parallel_tool_calls"] = false;
            }
            if (!assistantSettings.ReasoningEffort.Equals("none", StringComparison.OrdinalIgnoreCase)
                && assistantSettings.Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
                request["reasoning_effort"] = assistantSettings.ReasoningEffort;

            var response = await PostAsync(assistantSettings, apiKey, request, cancellationToken);
            var usage = response["usage"] as JsonObject;
            totalInput += ReadInt(usage, "prompt_tokens");
            totalOutput += ReadInt(usage, "completion_tokens");

            var firstChoice = (response["choices"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var message = firstChoice?["message"] as JsonObject
                          ?? throw new InvalidOperationException("A OpenAI não retornou uma mensagem válida.");
            var toolCalls = message["tool_calls"] as JsonArray;
            var content = ReadContent(message["content"]);

            if (toolCalls is null || toolCalls.Count == 0)
            {
                finalText = string.IsNullOrWhiteSpace(content) ? "A API respondeu sem conteúdo de texto." : CleanAssistantText(content.Trim());
                break;
            }

            messages.Add(message.DeepClone());
            foreach (var node in toolCalls.OfType<JsonObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var callId = node["id"]?.GetValue<string>() ?? string.Empty;
                var function = node["function"] as JsonObject;
                var name = function?["name"]?.GetValue<string>() ?? string.Empty;
                var argumentsText = function?["arguments"]?.GetValue<string>() ?? "{}";
                JsonObject arguments;
                try { arguments = JsonNode.Parse(argumentsText) as JsonObject ?? new JsonObject(); }
                catch { arguments = new JsonObject(); }

                var execution = await _data.ExecuteAsync(name, arguments, assistantSettings, cancellationToken);
                if (!string.IsNullOrWhiteSpace(execution.Summary)) toolSummaries.Add(execution.Summary);
                pendingActions.AddRange(execution.PendingActions);
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = callId,
                    ["content"] = execution.OutputJson
                });
            }
        }

        if (string.IsNullOrWhiteSpace(finalText))
            throw new InvalidOperationException("O limite de etapas internas do assistente foi atingido antes de produzir a resposta. Tente formular a solicitação de forma mais direta.");

        var record = AssistantStorageService.CalculateUsage(assistantSettings.Model, totalInput, totalOutput, assistantSettings.DollarRate);
        await _storage.RecordUsageAsync(record);
        return new AssistantApiResult
        {
            Text = finalText,
            InputTokens = totalInput,
            OutputTokens = totalOutput,
            EstimatedCostBrl = record.EstimatedCostBrl,
            ToolSummaries = toolSummaries.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PendingActions = pendingActions
        };
    }

    public async Task<string> RewriteTextAsync(
        string text,
        string instruction,
        AssistantSettings assistantSettings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var apiKey = _credentials.ReadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("A chave da OpenAI ainda não foi configurada.");

        var usageBefore = await _storage.GetCurrentMonthUsageAsync(assistantSettings);
        if (assistantSettings.HardBudgetLimit && assistantSettings.MonthlyBudgetBrl > 0
            && usageBefore.EstimatedCostBrl >= assistantSettings.MonthlyBudgetBrl)
            throw new InvalidOperationException($"O limite interno mensal de {assistantSettings.MonthlyBudgetBrl:C2} foi atingido.");

        var payload = new JsonObject
        {
            ["model"] = assistantSettings.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "Você é um revisor profissional de português brasileiro, conforme o Acordo Ortográfico vigente. Corrija ortografia, acentuação, concordância, regência, pontuação, clareza e coesão. Preserve nomes, números, siglas, referências e fatos. Retorne somente o texto revisado, sem explicações, aspas ou títulos adicionais."
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"INSTRUÇÃO ESPECÍFICA: {instruction}\n\nTEXTO:\n{text}"
                }
            },
            ["max_completion_tokens"] = Math.Min(Math.Max(800, assistantSettings.MaxOutputTokens), 5000)
        };

        var response = await PostAsync(assistantSettings, apiKey, payload, cancellationToken);
        var usage = response["usage"] as JsonObject;
        var input = ReadInt(usage, "prompt_tokens");
        var output = ReadInt(usage, "completion_tokens");
        var message = (response["choices"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["message"] as JsonObject;
        var revised = message is null ? string.Empty : ReadContent(message["content"]).Trim();
        if (string.IsNullOrWhiteSpace(revised))
            throw new InvalidOperationException("A API não retornou o texto revisado.");

        await _storage.RecordUsageAsync(AssistantStorageService.CalculateUsage(
            assistantSettings.Model, input, output, assistantSettings.DollarRate));
        return revised;
    }

    public async Task TestAsync(AssistantSettings settings, string? temporaryApiKey = null, CancellationToken cancellationToken = default)
    {
        var apiKey = string.IsNullOrWhiteSpace(temporaryApiKey) ? _credentials.ReadApiKey() : temporaryApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Informe ou salve uma chave da API antes de testar.");
        var request = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "Responda apenas com a palavra OK." },
                new JsonObject { ["role"] = "user", ["content"] = "Teste de conexão e compatibilidade das ferramentas do SIGFUR." }
            },
            ["max_completion_tokens"] = 32
        };
        if (settings.EnableLocalDataTools)
        {
            request["tools"] = _data.BuildToolDefinitions();
            request["tool_choice"] = "none";
        }
        await PostAsync(settings, apiKey, request, cancellationToken);
    }

    private JsonArray BuildMessages(
        IReadOnlyList<AssistantConversationMessage> history,
        string userPrompt,
        IReadOnlyList<AssistantAttachmentItem> attachments,
        AssistantSettings assistantSettings,
        UiProfile profile,
        string bulletinContext,
        string paymentRunContext,
        string webResearchContext)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = BuildSystemPrompt(profile, assistantSettings, bulletinContext, paymentRunContext, webResearchContext)
            }
        };

        foreach (var item in history
                     .Where(x => !x.IsError && (x.Role.Equals("user", StringComparison.OrdinalIgnoreCase) || x.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)))
                     .TakeLast(assistantSettings.MaxHistoryMessages))
        {
            messages.Add(new JsonObject { ["role"] = item.Role.ToLowerInvariant(), ["content"] = item.Content });
        }

        var input = new StringBuilder(userPrompt.Trim());
        if (attachments.Count > 0)
        {
            input.AppendLine().AppendLine();
            input.AppendLine("ARQUIVOS ANEXADOS PELO OPERADOR:");
            var remainingCharacters = 80_000;
            foreach (var attachment in attachments.Take(5))
            {
                var content = assistantSettings.RedactSensitiveData
                    ? AssistantAttachmentService.RedactSensitiveData(attachment.ExtractedText)
                    : attachment.ExtractedText;
                if (remainingCharacters <= 0) break;
                if (content.Length > remainingCharacters)
                    content = content[..remainingCharacters] + "\n[Conteúdo total dos anexos limitado pelo SIGFUR.]";
                remainingCharacters -= content.Length;
                input.AppendLine($"\n--- INÍCIO DO ARQUIVO: {attachment.FileName} ---");
                input.AppendLine(content);
                input.AppendLine($"--- FIM DO ARQUIVO: {attachment.FileName} ---");
            }
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = input.ToString() });
        return messages;
    }

    private static string BuildSystemPrompt(UiProfile profile, AssistantSettings settings, string bulletinContext, string paymentRunContext, string webResearchContext)
    {
        var custom = string.IsNullOrWhiteSpace(settings.OperatorInstructions)
            ? string.Empty
            : $"\nINSTRUÇÕES ADICIONAIS DO OPERADOR:\n{settings.OperatorInstructions.Trim()}\n";
        var learned = string.IsNullOrWhiteSpace(bulletinContext) ? string.Empty : $"\n{bulletinContext.Trim()}\n";
        var paymentContext = string.IsNullOrWhiteSpace(paymentRunContext) ? string.Empty : $"\n{paymentRunContext.Trim()}\n";
        var internetContext = string.IsNullOrWhiteSpace(webResearchContext) ? string.Empty : $"\nPESQUISA NA INTERNET JÁ REALIZADA PELO SIGFUR:\n{webResearchContext.Trim()}\n";
        return $$"""
Você é o Assistente SIGFUR, integrado ao Sistema Integrado de Gestão do Furriel.
Data atual: {{DateTime.Today:dd/MM/yyyy}}. Organização Militar configurada: {{profile.Organization}}.
Operador: {{profile.Rank}} {{profile.Operator}}. Função: {{profile.Function}}.
Comandante cadastrado: {{profile.CommanderRank}} {{profile.CommanderName}}.

MISSÃO
- Responder em português brasileiro, de forma profissional, objetiva e administrativa.
- Não use Markdown decorativo: não use **negrito**, ***asteriscos***, títulos com ###, nem frases como "Resposta direta:". Use texto limpo, títulos simples e listas com marcador "•" quando necessário.
- Começar pelo resultado encontrado no sistema. Depois apresente conferência, fonte, pendência ou orientação em blocos curtos.
- Quando o operador pedir quantidade, efetivo, férias, boletim, ADT Furriel, nota, assunto, contracheque, conferência, lembrete ou dado de militar, use primeiro as ferramentas locais. Não diga que não há consulta se existir ferramenta apropriada.
- A IA nunca deve inventar resultado local. Redija a resposta somente com os registros retornados pelas ferramentas; se vier apenas menção textual solta, classifique como possível referência, não como certeza.
- Resultados estruturados têm prioridade sobre texto indexado. Nome completo, CPF, Prec-CP e identidade têm prioridade sobre nome de guerra. Nome de guerra isolado serve apenas como sugestão.
- Para pergunta sobre uma pessoa, priorize consulta_operacional_militar para cruzar banco de dados, férias, boletins, aditamentos, documentos, contracheques e lembretes.
- Para férias, corrida de pagamento, adicional de férias, pagamento pendente ou alguém que entrou depois no plano, use consultar_alertas_corrida_pagamento. A regra operacional é: férias iniciando em um mês entram no pagamento do mês anterior; 1ª corrida no dia 15 do mês anterior à competência e 2ª corrida no 3º dia útil da competência.
- Quando houver pendência de férias no contexto operacional, responda primeiro à pergunta do operador e, no final, inclua um bloco curto "Atenção da corrida de pagamento" com quem precisa ser pago/conferido, a competência e as datas das corridas. Não transforme isso em textão.
- Para Auxílio-Transporte, consultar a ferramenta própria e informar linhas, número, nome/trajeto, tarifa e memória de cálculo quando disponíveis.
- Para conferência de pagamento, use conferir_pagamento_ia e destaque quem não recebeu, quem está divergente, quem está sem contracheque e quem ficou OK.
- Para lembretes pedidos pelo operador, use criar_lembrete_operacional e confirme a criação de forma direta.
- Auxiliar com DIEx, mensagens, orientações de pagamento, boletins, férias, contracheques, lembretes, faltas/atrasos, escala de serviço, conferências e documentos.
- Diferencie claramente: Encontrado no SIGFUR, Conferência realizada, Pendências e Próximo passo.
- Nunca invente BI, data, pessoa, valor, rubrica, artigo, portaria, documento ou procedimento.
- Quando faltarem dados, diga exatamente o que precisa ser conferido, sem se alongar.
- Em assuntos de pagamento ou legislação, priorize a biblioteca local de legislação quando houver documentos indexados. Cite nome da norma/página retornada pela ferramenta; quando não houver fonte local, use a pesquisa de internet do SIGFUR quando ela vier no contexto e cite claramente domínio, título e link/fonte.
- Perguntas gerais ou simples não devem virar pesquisa de militar. Responda normalmente, consultando legislação local e internet quando o tema for pagamento, direito, lei, portaria, decreto, norma ou orientação administrativa.
- Separe sempre as fontes: "Fonte local" para PDF salvo/indexado no SIGFUR e "Fonte internet" para resultado pesquisado online.

DIEx
- Ao gerar minuta, use linguagem militar formal, clara e direta.
- Não invente número do DIEx, protocolo, referência ou autoridade. Use campos entre colchetes quando faltar informação.
- Estruture, quando adequado: assunto, referência, finalidade, exposição objetiva e solicitação/conclusão.
- Entregue texto pronto para revisão, sem comentários desnecessários antes da minuta.
- Quando houver encerramento de contato, use: “Coloco-me à disposição para eventuais esclarecimentos por intermédio do {{profile.Rank}} {{profile.Operator}}, pelo RITEX {{(string.IsNullOrWhiteSpace(settings.DiexRitex) ? "[RITEX]" : settings.DiexRitex)}}.”

DADOS E AÇÕES
- Use somente o mínimo de dados pessoais necessário.
- Dados pessoais ficam ocultos por padrão. Quando o operador pedir explicitamente CPF, PREC-CP, identidade, data de nascimento, telefone, e-mail ou endereço de uma pessoa específica, solicite exatamente esses campos na ferramenta local e informe o valor retornado.
- Não responda com justificativa genérica de sigilo quando a ferramenta autorizada retornar o dado pedido pelo operador.
- Nunca exponha dados pessoais que não tenham sido pedidos explicitamente ou que continuem ocultos pela ferramenta.
- Conta bancária e agência não são disponibilizadas ao assistente.
- Ferramentas de consulta são somente leitura.
- Nunca abra PDF, carteira, pasta, navegador, rota ou impressão por conta própria. A resposta deve apenas listar o que foi encontrado; as ações retornadas aparecem como links/botões para o operador abrir somente se quiser.
- Quando uma ferramenta local retornar PDF, boletim, aditamento, carteira, pasta ou documento com caminho/ação, cite BI/ADT, data, página, assunto/nota, militar e fonte quando disponíveis. Informe que o item ficará como link azul na conversa. Não escreva “não consigo abrir o boletim por conta própria”; o correto é deixar o operador clicar no link seguro.
- Impressão e outras ações operacionais exigem confirmação na interface; nunca afirme que algo foi impresso antes dessa confirmação.
- Ao pesquisar boletim de uma pessoa, confirme pelo nome completo/documento quando disponível. Se o nome for ambíguo ou não bater, diga que não conseguiu vincular com segurança em vez de escolher outro militar parecido.
- Ao citar boletim ou legislação, informe a referência retornada pela ferramenta.
- Quando o operador pedir “crie”, “faça”, “monte”, “me dá um corpo”, “texto de boletim”, “nota para BI” ou “publicação” sobre qualquer assunto administrativo, use primeiro gerar_corpo_boletim. Entregue o campo texto_pronto como minuta pronta, sem responder que “posso entregar”.
- Para criar, revisar ou orientar boletim/SIPPES, consulte gerar_corpo_boletim e, quando necessário, consultar_regras_boletim e pesquisar_legislacao_local. Trate campo obrigatório ausente como impedimento, nunca como detalhe opcional.
- Se a biblioteca local de legislação não tiver a norma, não invente artigo. Diga que o texto foi montado pelo modelo administrativo do SIGFUR e oriente importar/indexar a norma no módulo Legislação.
- Preserve o padrão profissional: título do direito e ação, enunciado com fato gerador e referência, identificação completa do militar, dados específicos do lançamento e fechamento “Em consequência”.
{{paymentContext}}
{{internetContext}}
{{learned}}
{{custom}}
""";
    }

    private static bool ShouldUseInternetResearch(string prompt, IReadOnlyList<AssistantAttachmentItem> attachments)
    {
        if (attachments.Count > 0) return false;
        var normalized = AssistantIntentDetector.Normalize(prompt);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        return AssistantIntentDetector.ContainsAny(normalized,
            "legislacao", "legislação", "lei", "decreto", "portaria", "norma", "artigo", "amparo legal", "base legal", "fundamento",
            "quem tem direito", "faz jus", "pode receber", "deve receber", "como funciona",
            "pagamento", "remuneracao", "remuneração", "soldo", "adicional", "auxilio", "auxílio", "gratificacao", "gratificação",
            "ferias", "férias", "fusex", "irrf", "pensão", "pensao", "cpex", "dgp", "sippes")
            && !AssistantIntentDetector.ContainsAny(normalized, "boletim", "adt", "aditamento", "contracheque de", "carteira", "abrir", "imprimir");
    }

    private async Task<(string Context, int InputTokens, int OutputTokens)> TryBuildInternetResearchContextAsync(
        string userPrompt,
        AssistantSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = new JsonObject
            {
                ["model"] = ResolveResponsesWebModel(settings.Model),
                ["instructions"] = "Você é um pesquisador administrativo militar. Use a ferramenta web_search para consultar preferencialmente fontes oficiais brasileiras e militares. Responda em português. Traga um resumo objetivo e uma seção 'Fontes da internet' com título, órgão/domínio e URL quando disponível. Não invente artigo, data ou portaria.",
                ["input"] = "Pesquise na internet, com prioridade para fontes oficiais, para apoiar esta pergunta do operador do SIGFUR: " + userPrompt,
                ["tools"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "web_search",
                        ["filters"] = new JsonObject
                        {
                            ["allowed_domains"] = new JsonArray
                            {
                                "gov.br",
                                "planalto.gov.br",
                                "in.gov.br",
                                "sgex.eb.mil.br",
                                "dgp.eb.mil.br",
                                "cpex.eb.mil.br",
                                "eb.mil.br",
                                "camara.leg.br",
                                "senado.leg.br"
                            }
                        }
                    }
                },
                ["tool_choice"] = "required",
                ["max_output_tokens"] = Math.Min(Math.Max(900, settings.MaxOutputTokens), 2200),
                ["store"] = false
            };

            if (!settings.ReasoningEffort.Equals("none", StringComparison.OrdinalIgnoreCase)
                && settings.Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
                payload["reasoning"] = new JsonObject { ["effort"] = settings.ReasoningEffort };

            var response = await PostResponsesAsync(settings, apiKey, payload, cancellationToken);
            var text = CleanAssistantText(ReadResponsesOutputText(response));
            if (string.IsNullOrWhiteSpace(text)) return (string.Empty, ReadResponsesUsage(response, "input_tokens"), ReadResponsesUsage(response, "output_tokens"));

            var sources = ReadResponsesSources(response);
            var builder = new StringBuilder();
            builder.AppendLine(text.Length > 7000 ? text[..7000].TrimEnd() + "..." : text);
            if (sources.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Fontes consultadas pelo web_search:");
                foreach (var source in sources.Take(8)) builder.AppendLine("• " + source);
            }

            return (builder.ToString().Trim(), ReadResponsesUsage(response, "input_tokens"), ReadResponsesUsage(response, "output_tokens"));
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha na pesquisa de internet do Assistente SIGFUR. A resposta seguirá com base local/IA, sem fonte online.", ex);
            return (string.Empty, 0, 0);
        }
    }

    private static string ResolveResponsesWebModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "gpt-5.5";
        var trimmed = model.Trim();
        if (trimmed.Contains("search", StringComparison.OrdinalIgnoreCase)) return "gpt-5.5";
        return trimmed;
    }

    private async Task<JsonObject> PostResponsesAsync(AssistantSettings settings, string apiKey, JsonObject payload, CancellationToken cancellationToken)
    {
        var baseUrl = settings.ApiBaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps
            || !baseUri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Por segurança, o Assistente SIGFUR aceita somente o endereço oficial https://api.openai.com/v1.");

        var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/responses");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("X-Client-Request-Id", Guid.NewGuid().ToString("N"));
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await _log.WriteAsync($"OpenAI Responses API retornou {(int)response.StatusCode}: {body}");
            throw new InvalidOperationException("A pesquisa de internet pela Responses API falhou: " + ExtractApiError(body));
        }

        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException("Resposta inválida recebida da OpenAI Responses API.");
    }

    private static string ReadResponsesOutputText(JsonObject response)
    {
        var parts = new List<string>();
        if (response["output"] is JsonArray output)
        {
            foreach (var item in output.OfType<JsonObject>())
            {
                if (item["content"] is not JsonArray content) continue;
                foreach (var contentItem in content.OfType<JsonObject>())
                {
                    var type = contentItem["type"]?.GetValue<string>() ?? string.Empty;
                    if (type.Equals("output_text", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("text", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = contentItem["text"]?.GetValue<string>() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                    }
                }
            }
        }
        return string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    private static int ReadResponsesUsage(JsonObject response, string key)
    {
        try { return response["usage"]?[key]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }

    private static List<string> ReadResponsesSources(JsonObject response)
    {
        var sources = new List<string>();
        if (response["sources"] is JsonArray sourceArray)
        {
            foreach (var node in sourceArray.OfType<JsonObject>())
            {
                var title = node["title"]?.GetValue<string>() ?? string.Empty;
                var url = node["url"]?.GetValue<string>() ?? string.Empty;
                var domain = node["domain"]?.GetValue<string>() ?? string.Empty;
                var label = string.Join(" — ", new[] { title, domain, url }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(label)) sources.Add(label);
            }
        }
        return sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string CleanAssistantText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Replace("***", string.Empty, StringComparison.Ordinal)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal);
        text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s*", string.Empty);
        text = Regex.Replace(text, @"(?m)^\s*[-*]\s+", "• ");
        text = Regex.Replace(text, @"(?i)^\s*Resposta direta\s*:\s*", string.Empty);
        return text.Trim();
    }

    private async Task<JsonObject> PostAsync(AssistantSettings settings, string apiKey, JsonObject payload, CancellationToken cancellationToken)
    {
        var baseUrl = settings.ApiBaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps
            || !baseUri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Por segurança, o Assistente SIGFUR aceita somente o endereço oficial https://api.openai.com/v1.");
        var endpoint = new Uri(baseUri.ToString().TrimEnd('/') + "/chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("X-Client-Request-Id", Guid.NewGuid().ToString("N"));
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = ExtractApiError(body);
            var friendly = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Chave da API inválida ou sem permissão.",
                System.Net.HttpStatusCode.TooManyRequests when message.Contains("quota", StringComparison.OrdinalIgnoreCase) => "A conta da API está sem saldo ou atingiu o limite de uso.",
                System.Net.HttpStatusCode.TooManyRequests => "A API atingiu um limite temporário de solicitações. Tente novamente.",
                System.Net.HttpStatusCode.BadRequest => "A API recusou a solicitação: " + message,
                _ => $"Falha na OpenAI ({(int)response.StatusCode}): {message}"
            };
            await _log.WriteAsync($"OpenAI API retornou {(int)response.StatusCode}: {body}");
            throw new InvalidOperationException(friendly);
        }
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException("Resposta inválida recebida da OpenAI.");
    }

    private static string ExtractApiError(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            return root?["error"]?["message"]?.GetValue<string>() ?? body;
        }
        catch { return string.IsNullOrWhiteSpace(body) ? "Erro sem detalhes." : body; }
    }

    private static int ReadInt(JsonObject? obj, string key)
    {
        try { return obj?[key]?.GetValue<int>() ?? 0; }
        catch { return 0; }
    }

    private static string ReadContent(JsonNode? node)
    {
        if (node is null) return string.Empty;
        if (node is JsonValue value)
        {
            try { return value.GetValue<string>(); } catch { return value.ToString(); }
        }
        if (node is JsonArray array)
            return string.Join("\n", array.OfType<JsonObject>().Select(x => x["text"]?.GetValue<string>() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)));
        return node.ToString();
    }
}
