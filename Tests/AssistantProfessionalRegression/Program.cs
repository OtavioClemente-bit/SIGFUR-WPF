using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SIGFUR.Wpf;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;
using SIGFUR.Wpf.Views;

internal static class Program
{
    private static int _checks;
    private static readonly AssistantResponsesOptions Options = new("gpt-5.6-luna", "medium", 6000, 2, 60000);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--scan-consequences")
        {
            ScanConsequences(args[1]);
            return;
        }
        RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--render")) Render(args.Last());
        Console.WriteLine($"All {_checks} professional assistant regression checks passed. No live API calls.");
    }

    private static void ScanConsequences(string source)
    {
        var notes = new List<IntelligentBulletinFinding>();
        var filesWithFurrielText = 0;
        var filesWithFurrielConsequence = 0;
        var furrielOutsideExtractedConsequence = new List<string>();
        List<string> files;
        if (File.Exists(source) && Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var root = JsonNode.Parse(File.ReadAllText(source));
            var items = root?["itens"] as JsonArray ?? root?["items"] as JsonArray ?? root as JsonArray ?? [];
            files = items.Select(item => item?["texto_path"]?.ToString() ?? item?["textCachePath"]?.ToString() ?? string.Empty)
                .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            files = Directory.EnumerateFiles(source, "*.txt", SearchOption.TopDirectoryOnly).ToList();
        }
        foreach (var file in files)
        {
            var pages = File.ReadAllText(file).Split('\f');
            var hasFurrielText = Regex.IsMatch(string.Join(' ', pages), @"\bfurriel\b", RegexOptions.IgnoreCase);
            if (hasFurrielText) filesWithFurrielText++;
            var header = pages.FirstOrDefault() ?? string.Empty;
            var number = Regex.Match(header, @"BOLETIM\s+INTERNO\s+N[ºO°]?\s*([0-9]{1,4}/[0-9]{4})", RegexOptions.IgnoreCase).Groups[1].Value;
            var extracted = IntelligentBulletinService.ExtractConsequencesFromPages(pages, number, "—", file);
            if (extracted.Any(note => IntelligentBulletinService.ConsequenceMentions(note, "Furriel"))) filesWithFurrielConsequence++;
            else if (hasFurrielText) furrielOutsideExtractedConsequence.Add(Path.GetFileName(file));
            notes.AddRange(extracted);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            arquivos = files.Count,
            arquivos_com_furriel_no_texto = filesWithFurrielText,
            arquivos_com_furriel_em_consequencia = filesWithFurrielConsequence,
            arquivos_com_furriel_fora_da_consequencia_extraida = furrielOutsideExtractedConsequence,
            consequencias = notes.Count,
            furriel = notes.Count(note => IntelligentBulletinService.ConsequenceMentions(note, "Furriel")),
            furriel_sem_assunto = notes.Count(note => note.Subject == "Assunto não identificado" && IntelligentBulletinService.ConsequenceMentions(note, "Furriel")),
            primeira_secao = notes.Count(note => IntelligentBulletinService.ConsequenceMentions(note, "1ª Seção")),
            chaves_unicas = notes.Select(note => note.ReviewStorageKey).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            assunto_nao_identificado = notes.Count(note => note.Subject == "Assunto não identificado"),
            assuntos_principais = notes.GroupBy(note => note.Subject).OrderByDescending(group => group.Count()).Take(15)
                .Select(group => new { assunto = group.Key, quantidade = group.Count() }),
            subitens_sem_assunto = notes.Where(note => note.Subject == "Assunto não identificado")
                .GroupBy(note => note.NoteTitle).OrderByDescending(group => group.Count()).Take(15)
                .Select(group => new { subitem = group.Key, quantidade = group.Count() }),
            subitens_furriel_sem_assunto = notes.Where(note => note.Subject == "Assunto não identificado" && IntelligentBulletinService.ConsequenceMentions(note, "Furriel"))
                .GroupBy(note => note.NoteTitle).OrderByDescending(group => group.Count()).Take(15)
                .Select(group => new { subitem = group.Key, quantidade = group.Count() })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Check(bool success, string label)
    {
        if (!success) throw new Exception("FAIL: " + label);
        _checks++;
        Console.WriteLine("PASS: " + label);
    }

    private static JsonObject Completed(string text) => new()
    {
        ["status"] = "completed", ["usage"] = new JsonObject { ["input_tokens"] = 20, ["output_tokens"] = 10 },
        ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) })
    };

    private static JsonObject Call(string name, string args, string id) => new()
    {
        ["status"] = "completed", ["output"] = new JsonArray(
            new JsonObject { ["type"] = "reasoning", ["id"] = "rs_" + id, ["summary"] = new JsonArray(), ["encrypted_content"] = "opaque" },
            new JsonObject { ["type"] = "function_call", ["call_id"] = id, ["name"] = name, ["arguments"] = args })
    };

    private static async Task RunAsync()
    {
        var tool = new JsonObject { ["type"] = "function", ["name"] = "lookup", ["parameters"] = new JsonObject { ["type"] = "object" } };
        var input = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Conferir" });
        var sent = new List<JsonObject>();
        var executed = 0; var accounted = 0;
        var responses = new Queue<JsonObject>([Call("lookup", "{}", "c1"), Call("lookup", "{}", "c2"), Completed("Verificado nas evidências")]);
        var answer = await AssistantResponsesProtocol.RunAsync(Options, "instructions", input, new JsonArray(tool),
            (r, ct) => { sent.Add((JsonObject)r.DeepClone()); return Task.FromResult(responses.Dequeue()); },
            (n, a, ct) => { executed++; return Task.FromResult("{\"value\":42}"); },
            r => { accounted++; return Task.CompletedTask; }, default);
        Check(answer.Contains("evidências") && executed == 1 && accounted == 3, "tool loop, duplicate call reuse, and every response accounted");
        Check(sent.All(x => x["store"]!.GetValue<bool>() == false && x["reasoning"]?["effort"]?.ToString() == "medium"), "Responses stateless reasoning configured");
        Check(sent[1]["input"]!.ToJsonString().Contains("encrypted_content") && sent[1]["input"]!.ToJsonString().Contains("function_call_output"), "reasoning and call-output pair carried across rounds");
        Check(sent.Last()["tool_choice"]!.ToString() == "none", "final bounded round requires a synthesis");

        var structuredRequest = AssistantResponsesProtocol.CreateRequest(Options with
        {
            PromptCacheKey = "sigfur-test-v2",
            ResponseSchema = AssistantStructuredSchemas.PaymentAudit
        }, "stable", input, []);
        Check(structuredRequest["store"]!.GetValue<bool>() == false
              && structuredRequest["truncation"]!.ToString() == "disabled"
              && structuredRequest["prompt_cache_key"]!.ToString() == "sigfur-test-v2"
              && structuredRequest["text"]?["format"]?["type"]!.ToString() == "json_schema",
            "structured output, prompt cache key, no storage and no silent truncation are encoded");
        var validStructured = Completed("{\"results\":[{\"item_id\":\"A\",\"status\":\"INCONCLUSIVO\",\"military\":\"\",\"finding\":\"faltam dados\",\"bulletin_evidence\":\"\",\"paystub_evidence\":\"\",\"difference\":null,\"recommended_action\":\"revisar\",\"confidence\":0.2,\"evidence_gap\":\"documento\"}]}");
        Check(AssistantResponsesProtocol.DeserializeStructured<PaymentAiAuditResponse>(validStructured).Results.Single().ItemId == "A",
            "valid structured output deserializes to typed DTO");
        try { AssistantResponsesProtocol.DeserializeStructured<PaymentAiAuditResponse>(Completed("not-json")); throw new Exception("invalid structured output accepted"); }
        catch (InvalidOperationException) { Check(true, "invalid structured output is rejected"); }

        accounted = 0;
        try
        {
            await AssistantResponsesProtocol.RunAsync(Options, "", input, [],
                (r, ct) => Task.FromResult(new JsonObject { ["status"] = "incomplete", ["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" } }),
                (n, a, ct) => throw new Exception("unexpected"), r => { accounted++; return Task.CompletedTask; }, default);
            throw new Exception("accepted incomplete response");
        }
        catch (InvalidOperationException ex) { Check(accounted == 1 && ex.Message.Contains("limite"), "incomplete response fails after recording usage"); }

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var touchedNetwork = false;
        try
        {
            await AssistantResponsesProtocol.RunAsync(Options, "", input, [],
                (r, ct) => { touchedNetwork = true; return Task.FromResult(Completed("bad")); },
                (n, a, ct) => throw new Exception(), r => Task.CompletedTask, cancel.Token);
        }
        catch (OperationCanceledException) { Check(!touchedNetwork, "cancellation prevents API call"); }

        var invalidCalls = new Queue<JsonObject>([Call("forbidden", "{}", "x1"), Call("lookup", "not-json", "x2"), Completed("Lacunas")]);
        executed = 0;
        await AssistantResponsesProtocol.RunAsync(Options, "", input, new JsonArray(tool.DeepClone()),
            (r, ct) => Task.FromResult(invalidCalls.Dequeue()),
            (n, a, ct) => { executed++; return Task.FromResult("{}"); }, r => Task.CompletedTask, default);
        Check(executed == 0, "unknown tools and malformed arguments never execute");
        Check(AssistantResponsesProtocol.BoundToolOutput(new string('x', 30000), 1000).Contains("parcial"), "oversized tool evidence explicitly marked partial");

        var web = AssistantOfficialResearch.BuildRequest(Options, new JsonObject { ["tema"] = "exercicios_anteriores", ["enfoque"] = "vigencia" });
        Check(web["tools"]?[0]?["filters"]?["allowed_domains"] is JsonArray && web["tool_choice"]!.ToString() == "required", "official research requires restricted web search");
        try
        {
            AssistantOfficialResearch.BuildRequest(Options, new JsonObject { ["tema"] = "ferias", ["enfoque"] = "geral", ["consulta"] = "PRIVATE NAME CPF" });
            throw new Exception("arbitrary query accepted");
        }
        catch (ArgumentException) { Check(true, "arbitrary private queries rejected at search boundary"); }
        Check(AssistantOfficialResearch.IsOfficialUrl("https://www.cpex.eb.mil.br/manual") && !AssistantOfficialResearch.IsOfficialUrl("https://eb.mil.br.evil.test/x") && !AssistantOfficialResearch.IsOfficialUrl("https://evil@eb.mil.br/x"), "source URLs validate full official hostname");

        var rows = Enumerable.Range(1, 9).Select(i => new PaymentConferenceResultRow { Military = "MILITAR TESTE " + i, Context = "Publicação sintética", PaymentType = "Férias" }).ToList();
        var batches = AssistantWorkflowService.BuildPaymentBatches(rows, true, 20000, 4);
        Check(batches.Count == 3 && batches.Sum(x => x.Items.Count) == 9 && batches.SelectMany(x => x.Items).Select(x => x.Id).Distinct().Count() == 9, "payment batches cover every item exactly once");
        var evidence = JsonSerializer.Serialize(AssistantWorkflowService.PaymentEvidence(rows[0], "ITEM 000001"));
        Check(JsonNode.Parse(evidence)?["valor_publicado"] is null && JsonNode.Parse(evidence)?["valor_no_contracheque"] is null, "unknown payment values remain null, never zero");
        Check(AssistantWorkflowService.MissingPaymentItems(batches[0], "[ITEM 000001]\nInconclusivo").Count == 3, "coverage reports missing individual answers");
        var missingIds = PaymentMonthlyAuditRules.ValidateReturnedIds(["A", "B"], new PaymentAiAuditResponse
        {
            Results = [new PaymentAiFinding { ItemId = "A" }]
        });
        Check(missingIds?.Contains("B") == true, "every item_id must return or structured coverage fails");
        var completeIds = PaymentMonthlyAuditRules.ValidateReturnedIds(["A", "B"], new PaymentAiAuditResponse
        {
            Results = [new PaymentAiFinding { ItemId = "A", Finding = "Evidência insuficiente" }, new PaymentAiFinding { ItemId = "B", Finding = "Evidência insuficiente" }]
        });
        Check(completeIds is null, "complete unique item_id set passes coverage validation");
        Check(PaymentMonthlyAuditRules.ValidateReturnedIds(["A"], new PaymentAiAuditResponse
        {
            Results = [new PaymentAiFinding { ItemId = "A", Status = "APROVADO", Finding = "texto", Confidence = 1 }]
        }) is not null, "status outside the closed audit vocabulary is rejected");
        rows[0].Context = new string('x', 30000);
        try { AssistantWorkflowService.BuildPaymentBatches(rows, false, 4000); throw new Exception("cut evidence"); }
        catch (InvalidOperationException) { Check(true, "oversized individual evidence rejected without clipping"); }
        var ea = new ExercisePreviousProcess();
        Check(ea.NonPaymentExplanation == "" && ea.RightMaterializationDocument == "", "new EA process has no fictional inherited cause");
        Check(AssistantWorkflowService.CaseFact(ExercisePreviousDefaults.ProfessionalNonPaymentGuide).Contains("NÃO CONSIDERAR"), "legacy template text excluded from case facts");
        Check(AssistantWorkflowService.EaPrompt(ea, "Motivo").Contains("CONFIRMAR CAUSA"), "EA prompt flags missing cause instead of inventing one");

        var today = new DateTime(2026, 9, 9);
        var items = new[]
        {
            new AssistantOperationalItem { Id = "old", DueDate = today.AddDays(-1) },
            new AssistantOperationalItem { Id = "soon", DueDate = today.AddDays(7) },
            new AssistantOperationalItem { Id = "later", DueDate = today.AddDays(8) },
            new AssistantOperationalItem { Id = "nodate" },
            new AssistantOperationalItem { Id = "urgent", Urgent = true }
        };
        Check(AssistantOperationsService.SelectAttention(items, today, 7).Select(x => x.Id).SequenceEqual(["old", "soon", "urgent"]), "alerts respect registered deadlines, lookahead, urgency and missing dates");
        Check(AssistantOperationsService.AlertKey(items[0], today) != AssistantOperationsService.AlertKey(items[0], today.AddDays(1)), "daily acknowledgment cannot suppress tomorrow's overdue alert");
        Check(AssistantOperationsService.IsPaymentCompleted(" CONCLUÍDO ") && AssistantOperationsService.IsPaymentCompleted("ok") && !AssistantOperationsService.IsPaymentCompleted("pendente"), "completed payment controls omitted from pending list");

        var legacy = JsonSerializer.Deserialize<AssistantSettings>("{\"ProviderMode\":\"Local\",\"LocalModelPath\":\"old.gguf\",\"MonthlyBudgetBrl\":15,\"Model\":\"gpt-5.4\"}")!;
        Check(legacy.Model == "gpt-5.4" && legacy.MonthlyBudgetBrl == 15 && !JsonSerializer.Serialize(legacy).Contains("LocalModel"), "legacy local configuration migrates without losing model or budget");
        Check(new AssistantSettings().Provider == "OpenAI" && AssistantCredentialService.IsDeepSeek("deepseek"), "provider defaults safely and recognizes DeepSeek case-insensitively");
        Check(new AssistantSettings().Model == "gpt-5.6-luna", "new configurations default to gpt-5.6-luna");
        var deepSeekUsage = AssistantStorageService.CalculateUsage("deepseek-v4-flash", 1_000_000, 1_000_000, 1, 0, "DeepSeek");
        Check(deepSeekUsage.Provider == "DeepSeek" && deepSeekUsage.EstimatedCostUsd == .42m, "DeepSeek V4 Flash usage applies official cache-miss estimate");
        var usage = AssistantStorageService.CalculateUsage("gpt-5.4-mini", 0, 0, 5, 3);
        Check(usage.EstimatedCostBrl == .15m && usage.WebSearchCalls == 3 && usage.PaidToolsCostUsd == .03m,
            "paid web research tools are separated and included in consumption estimate");
        Check(AssistantStorageService.CalculateUsage("gpt-5.4-mini", -100, -10, 5).EstimatedCostBrl == 0, "invalid negative token values cannot reduce budget use");
        var lunaUsage = AssistantStorageService.CalculateUsage("gpt-5.6-luna", 1_000_000, 1_000_000, 1, 0, "OpenAI", 250_000);
        Check(lunaUsage.UncachedInputTokens == 750_000 && lunaUsage.CachedInputTokens == 250_000
              && lunaUsage.EstimatedCostUsd == 1.355m, "Luna cost separates uncached input, cached input and output");

        var dataTools = (AssistantDataService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AssistantDataService));
        var rewriteRoute = AssistantToolRouter.Route("Reescreva este parágrafo com mais clareza.");
        var paymentRoute = AssistantToolRouter.Route("Faça a conferência de pagamento e localize a rubrica divergente.");
        Check(!rewriteRoute.HasTools && rewriteRoute.RecommendedReasoningEffort == "low", "rewrite receives no payment or data tools");
        Check(paymentRoute.HasTools && paymentRoute.ToolNames.Contains("conferir_pagamento_ia")
              && paymentRoute.ToolNames.Count < dataTools.BuildToolDefinitions().Count, "tool router materially reduces payment tool definitions");

        var buildInput = typeof(OpenAiAssistantService).GetMethod("BuildBoundedInput", BindingFlags.NonPublic | BindingFlags.Static)!;
        var privacySettings = new AssistantSettings();
        var bounded = (JsonArray)buildInput.Invoke(null, new object[] { new List<AssistantConversationMessage> { new() { Role = "user", Content = "CPF 123.456.789-00" } }, "CPF 123.456.789-00", new List<AssistantAttachmentItem>(), privacySettings, 3000 })!;
        Check(!bounded.ToJsonString().Contains("123.456.789-00"), "redaction applies to prompt and reused history");
        try
        {
            buildInput.Invoke(null, new object[] { new List<AssistantConversationMessage>(), new string('x', 90000), new List<AssistantAttachmentItem>(), privacySettings, 3000 });
            throw new Exception("oversized request accepted");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { Check(true, "oversized operator request rejected before API submission"); }

        var definitions = AssistantResponsesProtocol.ConvertTools(dataTools.BuildToolDefinitions());
        Check(definitions.OfType<JsonObject>().All(x =>
            (x["parameters"]?["properties"] as JsonObject)!.Select(p => p.Key).ToHashSet()
            .SetEquals((x["parameters"]?["required"] as JsonArray)!.Select(p => p!.ToString()))), "all strict tool schemas declare every property required");

        var recurringCurrent = new[] { new PaymentConferenceRubricHit { Cpf = "12345678900", Code = "NR0001", Value = 1000 } };
        var recurringPrevious = new[] { new PaymentConferenceRubricHit { Cpf = "12345678900", Code = "NR0001", Value = 1000 } };
        Check(PaymentMonthlyAuditRules.CompareRubricChanges(recurringCurrent, recurringPrevious, []).Count == 0,
            "unchanged recurring rubric creates no false publication alert");
        var changed = PaymentMonthlyAuditRules.CompareRubricChanges(
            [new PaymentConferenceRubricHit { Cpf = "12345678900", Code = "NR0001", Value = 1100 }], recurringPrevious, []);
        Check(changed.Single().ChangeType == "VALOR_ALTERADO" && !changed.Single().HasBulletinEvidence,
            "relevant paystub change without publication is detected in reverse audit");
        var publication = new PaymentConferenceExpectedItem { Cpf = "12345678900", ExpectedCodes = ["NR0001"] };
        Check(PaymentMonthlyAuditRules.CompareRubricChanges(
            [new PaymentConferenceRubricHit { Cpf = "12345678900", Code = "NR0001", Value = 1100 }], recurringPrevious, [publication]).Single().HasBulletinEvidence,
            "reverse audit recognizes matching bulletin evidence");
        Check(PaymentMonthlyAuditRules.IsDeterministicallyClosed(new PaymentConferenceResultRow
            { Origin = "PARSER_SIGFUR", Status = "ACHOU RUBRICA", Severity = "success", HasExpectedAmount = true, HasPaidAmount = true, ExpectedAmount = 1000, PaidAmount = 1000 }),
            "high-confidence exact parser result remains deterministically closed without an AI call");
        Check(PaymentMonthlyAuditRules.IsMissingPaystubReflection(new PaymentConferenceResultRow
            { Status = "NÃO ACHOU RUBRICA", BulletinPath = "BI.pdf", PaystubPath = "CC.pdf" }),
            "publication present in bulletin without a corresponding paystub rubric is detected");

        var ambiguous = PaymentConferenceService.DetectAmbiguousBlocks("BI_1.pdf",
            ["Pagamento atrasado do militar TESTE conforme rubrica AR0092, estrutura deliberadamente desconhecida."], []);
        Check(ambiguous.Count == 1 && ambiguous[0].Page == 1, "financial publication missed by traditional parser becomes an AI Rescue candidate");
        var rescueFixture = Completed("{\"items\":[{\"source_file\":\"BI_1.pdf\",\"bulletin_number\":\"1/2026\",\"bulletin_date\":\"09/09/2026\",\"page\":1,\"source_excerpt\":\"Pagamento atrasado\",\"military_name\":\"TESTE\",\"cpf\":\"\",\"prec_cp\":\"\",\"subject\":\"Férias\",\"financial_effect\":\"Atrasado\",\"reference_period\":\"\",\"expected_rubric_or_code\":\"AR0092\",\"expected_value\":null,\"confidence\":0.7,\"reasoning_summary\":\"efeito explícito\",\"requires_human_review\":true}]}");
        Check(AssistantResponsesProtocol.DeserializeStructured<AiRescueResponse>(rescueFixture).Items.Single().RequiresHumanReview,
            "AI Rescue structured fixture recovers a missed publication without auto-approval");
        var rescuedPaymentRow = new PaymentConferenceResultRow { Origin = "IA_RESCUE", Status = "REVISÃO NECESSÁRIA" };
        var rescuedBulletinFinding = new IntelligentBulletinFinding { InterpretationOrigin = "IA_RESCUE", RequiresHumanReview = true };
        Check(!rescuedPaymentRow.IsVerified && !rescuedBulletinFinding.Reviewed && rescuedBulletinFinding.RequiresHumanReview,
            "AI conclusions never mark payment or bulletin human review as completed");
        var partialInventory = new PaymentMonthlyInventory { ExpectedBulletins = 10, ProcessedBulletins = 9, ExpectedPages = 10, ProcessedPages = 9, ExpectedPaystubs = 1, FoundPaystubs = 1, ReadPaystubs = 1 };
        Check(!partialInventory.Complete && partialInventory.CoveragePercent < 100,
            "9 of 10 documents can never be declared a complete monthly audit");
        var missingBaseline = new PaymentMonthlyInventory { ExpectedBulletins = 1, ProcessedBulletins = 1, ExpectedPages = 1, ProcessedPages = 1, ExpectedPaystubs = 1, FoundPaystubs = 1, ReadPaystubs = 1, ExpectedPreviousPaystubs = 1 };
        Check(!missingBaseline.Complete && missingBaseline.CoveragePercent < 100,
            "missing previous-period baseline keeps the reverse monthly audit partial");

        var cacheRoot = Path.Combine(Path.GetTempPath(), "sigfur-ai-cache-test-" + Guid.NewGuid().ToString("N"));
        var cachePaths = new AppPaths(cacheRoot);
        var interpretationCache = new AssistantInterpretationCache(cachePaths);
        var evidenceFile = Path.Combine(cacheRoot, "evidence.txt"); Directory.CreateDirectory(cacheRoot);
        await File.WriteAllTextAsync(evidenceFile, "version one");
        var cacheKey1 = await interpretationCache.BuildKeyAsync(evidenceFile, "page:1", "parser-v1", "prompt-v1", "gpt-5.6-luna");
        await interpretationCache.SaveAsync(cacheKey1, new AiRescueResponse { Items = [new AiRescuePublication { SourceExcerpt = "version one" }] });
        Check((await interpretationCache.TryLoadAsync<AiRescueResponse>(cacheKey1))?.Items.Count == 1,
            "identical document and versions reuse local interpretation cache without API");
        await File.WriteAllTextAsync(evidenceFile, "version two");
        var cacheKey2 = await interpretationCache.BuildKeyAsync(evidenceFile, "page:1", "parser-v1", "prompt-v1", "gpt-5.6-luna");
        Check(cacheKey1 != cacheKey2 && await interpretationCache.TryLoadAsync<AiRescueResponse>(cacheKey2) is null,
            "changing the source file invalidates interpretation cache");

        var directory = Path.Combine(Path.GetTempPath(), "sigfur-operations-test-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(directory);
        var json = new JsonFileService();
        var reminders = new ReminderService(paths, json);
        await reminders.SaveAsync(new ReminderRecord { Title = "OPEN TEST", Date = "09/09/2026" });
        await reminders.SaveAsync(new ReminderRecord { Title = "DONE TEST", Completed = true });
        await json.SaveNodeAsync(paths.PaymentRemindersFile, new JsonObject { ["items"] = new JsonArray(
            new JsonObject { ["id"] = "p1", ["descricao"] = "PENDING PAYMENT", ["status"] = "pendente", ["prazo"] = "10/09/2026" },
            new JsonObject { ["id"] = "p2", ["descricao"] = "DONE PAYMENT", ["status"] = "ok" }) });
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={paths.DatabaseFile}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE ea_processos (id INTEGER, posto_grad TEXT, nome_completo TEXT, motivo_pagamento TEXT, cpex_status TEXT, cpex_motivo_relatorio TEXT, pago INTEGER); INSERT INTO ea_processos VALUES (1,'TEST','PENDING EA','Test case','Pendente','',0),(2,'TEST','PAID EA','','','',1);";
            await command.ExecuteNonQueryAsync();
        }
        var operations = await new AssistantOperationsService(paths, reminders, json, new LogService(paths)).BuildSnapshotAsync();
        Check(operations.Warnings.Count == 0 && operations.Items.Count == 3 && operations.Items.Single(x => x.Module == "Exercícios anteriores").DueDate is null,
            "operational snapshot integrates reminders, payments and unpaid EA without invented deadlines");
        var disabled = await dataTools.ExecuteAsync("consultar_efetivo", new JsonObject(), new AssistantSettings { EnableLocalDataTools = false }, default);
        Check(disabled.OutputJson.Contains("desativadas"), "module permission enforced at tool execution boundary");
        var proposed = await dataTools.ExecuteAsync("criar_lembrete_operacional", new JsonObject
        { ["titulo"] = "Lembrete de teste", ["data"] = "10/09/2026", ["prioridade"] = "Normal", ["descricao"] = "CPF 123.456.789-00" }, new AssistantSettings(), default);
        Check(proposed.PendingActions.Single().Type == "create_reminder" && !JsonNode.Parse(proposed.OutputJson)!["salvo"]!.GetValue<bool>()
              && !proposed.OutputJson.Contains("123.456.789-00"), "reminder is a reviewable draft with redacted API output, not an automatic write");

        var bulletinPage = """
            FÉRIAS REGULAMENTARES
            2) Inclusão
            Seja incluído no Plano de Férias o período dos militares abaixo.
            1º Sgt MILITAR UM
            2º Sgt MILITAR DOIS
            Em consequência:
            o Furriel realize os ajustes necessários;
            -
            a 1ª Seção atualize os assentamentos.
            3) Alteração
            Seja alterado o período regulamentar.
            Em consequência, a 2ª Seção tome conhecimento.
            """;
        var consequenceNotes = IntelligentBulletinService.ExtractConsequencesFromPages(
            [bulletinPage], "123/2026", "09/09/2026", "BI_123.pdf");
        Check(consequenceNotes.Count == 2 && consequenceNotes[0].Military.Length == 0,
            "consequence index creates one note instead of duplicating rows for mentioned military personnel");
        Check(consequenceNotes[0].DisplaySubject.Contains("FÉRIAS REGULAMENTARES")
              && consequenceNotes[0].DisplaySubject.Contains("Inclusão")
              && consequenceNotes[0].NoteText.Contains("MILITAR UM")
              && consequenceNotes[0].NoteText.Contains("MILITAR DOIS"),
            "consequence note preserves its professional subject, subitem and complete reading context");
        Check(IntelligentBulletinService.ConsequenceMentions(consequenceNotes[0], "Furriel")
              && IntelligentBulletinService.ConsequenceMentions(consequenceNotes[0], "1ª Seção")
              && !IntelligentBulletinService.ConsequenceMentions(consequenceNotes[0], "2ª Seção")
              && IntelligentBulletinService.ConsequenceMentions(consequenceNotes[1], "2ª Seção"),
            "editable responsible-party filter searches only the corresponding consequence block");
        var extractedAgain = IntelligentBulletinService.ExtractConsequencesFromPages(
            [bulletinPage], "123/2026", "09/09/2026", "renamed.pdf");
        Check(consequenceNotes[0].ReviewStorageKey == extractedAgain[0].ReviewStorageKey
              && consequenceNotes[0].ReviewStorageKey != consequenceNotes[1].ReviewStorageKey,
            "acknowledgement key survives file rename and remains unique per consequence note");
        var crossPage = IntelligentBulletinService.ExtractConsequencesFromPages(
            ["LICENCIAMENTO\nMilitar de teste\nEm consequência:",
             "(Continuação do BI)\no Furriel exclua o militar da folha de pagamento;\n-\nos interessados tomem conhecimento.\nb. INSPEÇÃO DE SAÚDE"],
            "124/2026", "10/09/2026", "BI_124.pdf");
        Check(crossPage.Count == 1 && IntelligentBulletinService.ConsequenceMentions(crossPage[0], "Furriel")
              && !crossPage[0].ConsequenceText.Contains("INSPEÇÃO DE SAÚDE"),
            "consequence recipient split across two PDF pages is captured without leaking into the next subject");
    }

    private static void Render(string outputDirectory)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var testRoot = Path.Combine(Path.GetTempPath(), "sigfur-assistant-ui-" + Guid.NewGuid().ToString("N"));
        var app = new App();
        app.InitializeComponent();
        typeof(App).GetMethod("InitializeServices", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [testRoot]);
        var chat = new AssistantChatWindow();
        chat.Messages.Add(new AssistantMessageView { Message = new AssistantConversationMessage { Content = "Encontrei uma divergência que precisa de revisão.\n\n• Militar de teste: valor publicado diferente da rubrica lida.\n• Manual SIPPES: consulte a página indicada na fonte.\n• Falta confirmar a competência do lançamento." } });
        Capture(chat, "assistant-chat.png");
        chat.OperationalItems.Add(new AssistantOperationalItem { Module = "Pagamento", Title = "Conferir férias — exemplo sintético", DueDate = DateTime.Today });
        chat.ShowOperations();
        Capture(chat, "assistant-pendencias.png");
        var settings = new AssistantSettingsWindow(App.AssistantStorage, App.AssistantCredentials, App.Assistant);
        Capture(settings, "assistant-settings.png");

        void Capture(Window window, string name)
        {
            var root = (FrameworkElement)window.Content;
            var width = window.Width - 16;
            var height = window.Height - 38;
            root.Width = double.NaN; root.Height = double.NaN;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)app.FindResource("AppBackgroundBrush"), null, new Rect(0, 0, width, height));
            bitmap.Render(background);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(outputDirectory, name)); encoder.Save(stream);
            Console.WriteLine("RENDER: " + name);
        }
    }
}
