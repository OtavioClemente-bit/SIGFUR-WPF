using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

// Stateless Responses protocol, separate from WPF/storage so its failure paths can be tested offline.
internal sealed record AssistantResponsesOptions(string Model, string ReasoningEffort, int MaxOutputTokens, int MaxToolRounds,
    int MaxContextCharacters, string PromptCacheKey = "", AssistantJsonSchema? ResponseSchema = null);
internal sealed record AssistantWebSource(string Title, string Url);

internal static class AssistantResponsesProtocol
{
    internal static JsonObject CreateRequest(AssistantResponsesOptions options, string instructions, JsonArray input, JsonArray tools)
    {
        var request = new JsonObject
        {
            ["model"] = options.Model.Trim(),
            ["instructions"] = instructions,
            ["max_output_tokens"] = Math.Clamp(options.MaxOutputTokens, 512, 16_000),
            ["store"] = false,
            ["truncation"] = "disabled",
            ["include"] = new JsonArray("reasoning.encrypted_content")
        };
        if (!string.IsNullOrWhiteSpace(options.PromptCacheKey))
            request["prompt_cache_key"] = options.PromptCacheKey.Trim();
        if (options.ResponseSchema is not null)
            request["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = options.ResponseSchema.Name,
                    ["strict"] = options.ResponseSchema.Strict,
                    ["schema"] = options.ResponseSchema.Schema.DeepClone()
                },
                ["verbosity"] = "low"
            };
        if (options.Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || options.Model.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)
            || options.Model.StartsWith("o", StringComparison.OrdinalIgnoreCase))
            request["reasoning"] = new JsonObject { ["effort"] = options.ReasoningEffort };
        if (tools.Count > 0)
        {
            request["tools"] = tools.DeepClone();
            request["tool_choice"] = "auto";
            request["parallel_tool_calls"] = false;
        }
        else request["tool_choice"] = "none";
        // O conteúdo variável entra por último; regras, schema e ferramentas formam
        // o prefixo estável compartilhado entre auditorias do mesmo fluxo.
        request["input"] = input.DeepClone();
        return request;
    }

    internal static JsonArray ConvertTools(JsonArray definitions)
    {
        var result = new JsonArray();
        foreach (var tool in definitions.OfType<JsonObject>())
        {
            var definition = tool["function"] as JsonObject ?? tool;
            if (string.IsNullOrWhiteSpace(ReadString(definition, "name"))) continue;
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = definition["name"]?.DeepClone(),
                ["description"] = definition["description"]?.DeepClone(),
                ["parameters"] = definition["parameters"]?.DeepClone(),
                ["strict"] = definition["strict"]?.DeepClone() ?? JsonValue.Create(true)
            });
        }
        return result;
    }

    internal static async Task<string> RunAsync(
        AssistantResponsesOptions options,
        string instructions,
        JsonArray initialInput,
        JsonArray tools,
        Func<JsonObject, CancellationToken, Task<JsonObject>> send,
        Func<string, JsonObject, CancellationToken, Task<string>> execute,
        Func<JsonObject, Task> accountUsage,
        CancellationToken cancellationToken)
    {
        var groups = new List<JsonArray>();
        var completedCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowedNames = tools.OfType<JsonObject>().Select(x => ReadString(x, "name")).ToHashSet(StringComparer.Ordinal);
        var maxRounds = Math.Clamp(options.MaxToolRounds, 1, 12);
        var contextLimit = Math.Clamp(options.MaxContextCharacters, 20_000, 120_000);
        var omittedGroups = 0;
        for (var round = 0; round <= maxRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Evict complete, already answered tool groups. Never split a call/result or encrypted reasoning item.
            while (groups.Count > 0 && instructions.Length + VisibleCharacters(initialInput) + groups.Sum(VisibleCharacters) > contextLimit - 1000)
            {
                groups.RemoveAt(0);
                omittedGroups++;
            }
            var input = (JsonArray)initialInput.DeepClone();
            if (omittedGroups > 0)
                input.Add(new JsonObject { ["role"] = "user", ["content"] = $"CONTROLE DE CONTEXTO: {omittedGroups} etapa(s) antiga(s) omitida(s). Baseie conclusões apenas nas evidências ainda presentes; se faltar evidência, registre a lacuna." });
            foreach (var group in groups)
                foreach (var item in group) input.Add(item?.DeepClone());
            var lastRound = round == maxRounds;
            var roundInstructions = instructions + (lastRound
                ? "\nO limite de consultas desta solicitação foi atingido. Entregue agora a síntese dos fatos comprovados e das lacunas. Não declare concluída uma auditoria incompleta e não alegue executar novas ações."
                : string.Empty);
            var request = CreateRequest(options, roundInstructions, input, lastRound ? new JsonArray() : tools);
            if (request.ToJsonString().Length > 1_500_000)
                throw new InvalidOperationException("O contexto interno excedeu o limite de segurança. Reduza o lote de documentos ou inicie uma nova conversa.");
            var response = await send(request, cancellationToken);
            // Persist usage before checking status, cancellation, parsing or running any local tool.
            await accountUsage(response);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCompleted(response);
            var output = response["output"] as JsonArray ?? new JsonArray();
            var calls = output.OfType<JsonObject>().Where(x => ReadString(x, "type") == "function_call").ToList();
            if (calls.Count == 0)
            {
                var text = ReadOutputText(response);
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("A API concluiu a solicitação sem uma resposta de texto. Nenhuma conclusão de auditoria foi registrada.");
                return text;
            }
            if (lastRound)
                throw new InvalidOperationException("A API não produziu a síntese após o limite de consultas. A conferência permanece incompleta.");

            var groupItems = (JsonArray)output.DeepClone();
            for (var index = 0; index < calls.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var call = calls[index];
                var callId = ReadString(call, "call_id");
                var name = ReadString(call, "name");
                if (string.IsNullOrWhiteSpace(callId))
                    throw new InvalidOperationException("A API retornou uma consulta sem identificador válido.");
                var rawArguments = ReadString(call, "arguments");
                var key = name + "\n" + rawArguments;
                string result;
                if (index >= 4) result = ErrorJson("Limite de consultas simultâneas excedido. Solicite uma consulta por etapa.");
                else if (!allowedNames.Contains(name)) result = ErrorJson("Ferramenta indisponível ou não autorizada para esta solicitação.");
                else if (!TryReadArguments(rawArguments, out var arguments)) result = ErrorJson("Argumentos JSON inválidos. Corrija os campos e tente a ferramenta novamente.");
                else if (completedCalls.TryGetValue(key, out var previousResult)) result = previousResult;
                else
                {
                    result = await execute(name, arguments!, cancellationToken);
                    result = BoundToolOutput(result, Math.Min(14_000, contextLimit / 4));
                    completedCalls[key] = result;
                }
                groupItems.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = result });
            }
            groups.Add(groupItems);
        }
        throw new InvalidOperationException("A conferência não pôde ser concluída no limite de consultas.");
    }

    internal static void EnsureCompleted(JsonObject response)
    {
        var status = ReadString(response, "status");
        if (status == "completed") return;
        if (status == "incomplete")
        {
            var reason = ReadString(response["incomplete_details"] as JsonObject, "reason");
            throw new InvalidOperationException(reason == "max_output_tokens"
                ? "A IA atingiu o limite de saída antes de concluir. Aumente o limite de resposta nas configurações ou reduza o lote. Os tokens utilizados foram contabilizados; nenhuma conclusão parcial foi tratada como auditoria concluída."
                : "A resposta da IA foi interrompida antes de concluir. Motivo: " + reason + ". A conferência permanece incompleta.");
        }
        throw new InvalidOperationException("A OpenAI não concluiu a resposta (estado: " + (string.IsNullOrWhiteSpace(status) ? "ausente" : status) + "). "
                                            + ReadString(response["error"] as JsonObject, "message"));
    }

    internal static string ReadOutputText(JsonObject response)
    {
        var parts = new List<string>();
        foreach (var message in (response["output"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (ReadString(message, "type") != "message" || message["content"] is not JsonArray content) continue;
            foreach (var item in content.OfType<JsonObject>())
            {
                if (ReadString(item, "type") == "output_text") parts.Add(ReadString(item, "text"));
                if (ReadString(item, "type") == "refusal") parts.Add(ReadString(item, "refusal"));
            }
        }
        return string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    internal static T DeserializeStructured<T>(JsonObject response)
    {
        EnsureCompleted(response);
        var json = ReadOutputText(response);
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("A API concluiu sem retornar o JSON estruturado esperado.");
        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidOperationException("O JSON estruturado retornado está vazio.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("A resposta estruturada da IA é inválida e não foi aceita pelo SIGFUR.", ex);
        }
    }

    internal static List<AssistantWebSource> ReadSources(JsonObject response)
    {
        var sources = new List<AssistantWebSource>();
        void Add(JsonObject source)
        {
            var url = ReadString(source, "url");
            if (!AssistantOfficialResearch.IsOfficialUrl(url)) return;
            var title = ReadString(source, "title");
            sources.Add(new AssistantWebSource(string.IsNullOrWhiteSpace(title) ? new Uri(url).Host : Clip(title, 180), url));
        }
        foreach (var item in (response["output"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (ReadString(item, "type") == "web_search_call" && item["action"]?["sources"] is JsonArray searched)
                foreach (var source in searched.OfType<JsonObject>()) Add(source);
            if (item["content"] is not JsonArray contents) continue;
            foreach (var content in contents.OfType<JsonObject>())
                if (content["annotations"] is JsonArray annotations)
                    foreach (var annotation in annotations.OfType<JsonObject>())
                        if (ReadString(annotation, "type") == "url_citation") Add(annotation);
        }
        return sources.DistinctBy(x => x.Url, StringComparer.OrdinalIgnoreCase).Take(12).ToList();
    }

    internal static string CleanText(string value)
    {
        var text = Regex.Replace(value, @"\uE200[^\uE201]*\uE201[^\uE202]*\uE202", string.Empty);
        text = text.Replace("***", string.Empty, StringComparison.Ordinal).Replace("**", string.Empty, StringComparison.Ordinal);
        text = Regex.Replace(text, @"(?m)^\s{0,3}#{1,6}\s*", string.Empty);
        text = Regex.Replace(text, @"(?m)^\s*[-*]\s+", "• ");
        return text.Trim();
    }

    internal static string Clip(string? text, int length)
    {
        text ??= string.Empty;
        if (text.Length <= Math.Max(0, length)) return text;
        const string suffix = "\n[Conteúdo limitado; análise parcial.]";
        if (length <= suffix.Length) return suffix[..Math.Max(0, length)];
        var end = length - suffix.Length;
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end] + suffix;
    }

    internal static string BoundToolOutput(string value, int limit)
        => value.Length <= limit ? value : new JsonObject
        {
            ["parcial"] = true,
            ["aviso"] = "Resultado excede o contexto. Não conclua sobre registros omitidos; use filtros/paginação para conferir o restante.",
            ["trecho"] = Clip(value, Math.Max(100, limit - 400))
        }.ToJsonString();

    internal static string ErrorJson(string message) => new JsonObject { ["erro"] = message, ["concluido"] = false }.ToJsonString();
    internal static string ReadString(JsonObject? node, string key) => node?[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : string.Empty;
    internal static int ReadInt(JsonObject? node, string key) => node?[key] is JsonValue value && value.TryGetValue<int>(out var result) ? Math.Max(0, result) : 0;
    internal static int ReadCachedInputTokens(JsonObject? usage)
        => ReadInt(usage?["input_tokens_details"] as JsonObject, "cached_tokens");
    internal static int WebSearchCalls(JsonObject response) => (response["output"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Count(x => ReadString(x, "type") == "web_search_call");

    private static bool TryReadArguments(string value, out JsonObject? arguments)
    {
        arguments = null;
        if (value.Length > 32_000) return false;
        try { arguments = JsonNode.Parse(value, documentOptions: new JsonDocumentOptions { MaxDepth = 20 }) as JsonObject; }
        catch (JsonException) { return false; }
        return arguments is not null;
    }

    private static int VisibleCharacters(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text.Length;
        if (node is JsonArray array) return array.Where(x => x is not null).Sum(x => VisibleCharacters(x!));
        if (node is JsonObject obj) return obj.Where(x => x.Key != "encrypted_content" && x.Value is not null).Sum(x => VisibleCharacters(x.Value!));
        return 0;
    }
}
