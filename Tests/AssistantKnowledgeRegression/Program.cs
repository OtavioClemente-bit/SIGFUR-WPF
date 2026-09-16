using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Services;

var root = Path.Combine(Path.GetTempPath(), "sigfur-knowledge-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + scenario);
    checks++;
    Console.WriteLine("PASS: " + scenario);
}

try
{
    var paths = new AppPaths(root);
    var log = new LogService();
    var legislation = new LegislationService(paths, log);
    var knowledge = new AssistantKnowledgeContextService(legislation, paths, log);
    var cacheFile = Path.Combine(paths.CacheDirectory, "AssistantKnowledge", "references-v1.json");
    var empty = await knowledge.BuildContextAsync("licença especial sintética");
    Check(empty.Sources.Count == 0 && empty.Gaps.Any(x => x.Contains("inconclusiva")), "missing manual explicitly inconclusive");

    var manualFile = Path.Combine(paths.LegislationDocumentsDirectory, "Manual_Tecnico_SIPPES_1.2.1_17-07-2026.txt");
    var normFile = Path.Combine(paths.LegislationDocumentsDirectory, "Lei_Sintetica_Transporte.txt");
    var longPrefix = string.Join(" ", Enumerable.Repeat("Introdução apenas com palavras irrelevantes.", 300));
    const string evidence = "TESTE APENAS: transporte sintético exige documento fictício ALFA123 para este cenário de regressão.";
    await File.WriteAllTextAsync(manualFile, longPrefix + " " + evidence);
    await File.WriteAllTextAsync(normFile, "NORMA FICTÍCIA SEM VALOR LEGAL: transporte sintético exige prova documental inventada para testes.");
    var first = await knowledge.BuildContextAsync("auxílio transporte sintético ALFA123");
    Check(first.Sources.Any(x => x.IsManual) && first.Sources.Any(x => !x.IsManual), "retrieve technical manual and complementary law separately");
    Check(first.Sources.Any(x => x.Excerpt.Contains("ALFA123")), "query-centred excerpt beyond long introduction");
    Check(first.Sources.All(x => x.SourceFingerprint.Length == 64 && x.Page == 1 && x.FileName.Length > 0), "traceable file, page, fingerprint for every excerpt");
    Check(first.ManualVersion.Contains("1.2.1") && first.ManualVersion.Contains("17-07-2026"), "manual version and edition date retained");
    Check(first.Gaps.Any(x => x.Contains("não a vigência legal")), "indexed copy is not represented as current law");
    var persisted = await File.ReadAllTextAsync(cacheFile);
    Check(!persisted.Contains("ALFA123") && !persisted.Contains("transporte") && !persisted.Contains("FileName"), "cache persists hashed query and locations only, no source text or query");

    var restarted = new AssistantKnowledgeContextService(legislation, paths, log);
    var repeated = await restarted.BuildContextAsync("auxílio transporte sintético ALFA123");
    Check(repeated.CacheHit && repeated.Sources.Count == first.Sources.Count, "new service restores persistent reference locations");

    var unrelated = await knowledge.BuildContextAsync("Manual SIPPES zebralunatica");
    Check(unrelated.Sources.Count == 0, "manual filename does not match irrelevant content query");

    var originalTime = File.GetLastWriteTimeUtc(manualFile);
    var originalFingerprint = first.Sources.First(x => x.IsManual).SourceFingerprint;
    var originalText = await File.ReadAllTextAsync(manualFile);
    await File.WriteAllTextAsync(manualFile, originalText.Replace("ALFA123", "BETA456", StringComparison.Ordinal));
    File.SetLastWriteTimeUtc(manualFile, originalTime);
    var tampered = await restarted.BuildContextAsync("auxílio transporte sintético ALFA123");
    Check(!tampered.CacheHit && !tampered.Sources.Any(x => x.IsManual)
        && tampered.Gaps.Any(x => x.Contains("Fonte alterada")), "same-size same-time file replacement invalidates cached source via SHA256");
    await legislation.IndexAllAsync(force: true);
    var reindexed = await restarted.BuildContextAsync("auxílio transporte sintético BETA456");
    Check(reindexed.Sources.Any(x => x.IsManual && x.Excerpt.Contains("BETA456") && x.SourceFingerprint != originalFingerprint), "reindex repairs changed source and returns new evidence");

    await File.AppendAllTextAsync(manualFile, " Atualização sintética GAMA789.");
    var modified = await restarted.BuildContextAsync("transporte GAMA789");
    Check(!modified.CacheHit && modified.Sources.Any(x => x.Excerpt.Contains("GAMA789")), "normal source change incrementally reindexes and invalidates corpus cache");
    File.Delete(manualFile);
    var deleted = await restarted.BuildContextAsync("transporte GAMA789");
    Check(!deleted.Sources.Any(x => x.IsManual) && deleted.Gaps.Any(x => x.Contains("Manual SIPPES")), "removed source cannot remain in cached evidence");

    await File.WriteAllTextAsync(cacheFile, "{ invalid json");
    var corrupted = await new AssistantKnowledgeContextService(legislation, paths, log).BuildContextAsync("transporte");
    Check(corrupted.Sources.Count > 0, "corrupt cache rebuilds without losing access to sources");
    var bounded = await knowledge.BuildContextAsync("transporte", maxSources: 100, maxCharacters: 2_000);
    Check(bounded.ContextText.Length <= 2_000 && bounded.Sources.Count <= 12, "context and source budgets enforced");
    foreach (var source in bounded.Sources)
        Check(bounded.ContextText.Contains(source.Reference), "structured sources only contain citations actually sent in context");

    var cacheOnly = new AssistantKnowledgeContextService(legislation, paths, log);
    for (var i = 0; i < 135; i++) await cacheOnly.BuildContextAsync("transporte " + i);
    using var cacheJson = JsonDocument.Parse(await File.ReadAllTextAsync(cacheFile));
    Check(cacheJson.RootElement.GetProperty("Entries").GetArrayLength() <= 128
        && new FileInfo(cacheFile).Length <= 512_000, "durable cache has bounded entries and bytes");
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var cancelled = false;
    try { await knowledge.BuildContextAsync("transporte", cancellationToken: cts.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "cancellation respected");
    Console.WriteLine($"All {checks} knowledge regression checks passed.");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}
