using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SIGFUR.Wpf.Models;
using SIGFUR.Wpf.Services;

static void Check(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException("Falha: " + scenario);
}

static AppPaths IsolatedPaths(string dataDirectory)
{
    // Evita executar migrações de pastas legadas durante o teste.
    var paths = (AppPaths)RuntimeHelpers.GetUninitializedObject(typeof(AppPaths));
    var field = typeof(AppPaths).GetField("<DataDirectory>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(AppPaths).FullName, "DataDirectory");
    field.SetValue(paths, dataDirectory);
    return paths;
}

static void SimulateNewProcess()
{
    var type = typeof(FurrielBulletinService);
    type.GetField("PersistentIndexMemory", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
    type.GetField("PersistentIndexMemoryPath", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, string.Empty);
}

if (args.Length == 2 && args[0].Equals("--audit-index", StringComparison.OrdinalIgnoreCase))
{
    var sourceIndex = Path.GetFullPath(args[1]);
    var auditDirectory = Path.Combine(Path.GetTempPath(), "sigfur-furriel-real-audit-" + Guid.NewGuid().ToString("N"));
    var auditModule = Path.Combine(auditDirectory, "boletim_furriel");
    Directory.CreateDirectory(auditModule);
    try
    {
        File.Copy(sourceIndex, Path.Combine(auditModule, "indice_furriel.json"), true);
        var auditPaths = IsolatedPaths(auditDirectory);
        var auditJson = new JsonFileService();
        var auditLog = new LogService(auditPaths);
        var auditSettings = new SettingsService(auditPaths, auditJson);
        var auditRepository = new MilitaryRepository(auditPaths, auditLog);
        var auditService = new FurrielBulletinService(auditPaths, auditSettings, auditRepository, auditLog);
        var migrated = await auditService.LoadIndexAsync();
        var mentions = migrated.Files.SelectMany(x => x.Mentions ?? []).ToList();
        var resolved = mentions.Count(x => !x.SubjectNoteDisplay.Equals("Menção no ADT Furriel", StringComparison.OrdinalIgnoreCase));
        var fragments = new HashSet<string>(["a", "e", "ao", "sem", "saque"], StringComparer.OrdinalIgnoreCase);
        var suspicious = mentions.Count(x => fragments.Contains(x.SubjectNoteDisplay.Trim()));
        Console.WriteLine($"AUDIT files={migrated.Files.Count} mentions={mentions.Count} resolved={resolved} fallback={mentions.Count - resolved} suspicious={suspicious} subjectIndex={migrated.SubjectIndex.Count}");
        foreach (var sample in mentions.Where(x => x.SubjectNoteDisplay.Equals("Menção no ADT Furriel", StringComparison.OrdinalIgnoreCase)).Take(15))
        {
            var text = string.Join(' ', (sample.NoteText ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            Console.WriteLine("FALLBACK " + text[..Math.Min(220, text.Length)]);
        }
    }
    finally
    {
        if (Directory.Exists(auditDirectory)) Directory.Delete(auditDirectory, true);
    }
    return;
}

var temporaryDirectory = Path.Combine(Path.GetTempPath(), "sigfur-furriel-search-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
try
{
    var paths = IsolatedPaths(temporaryDirectory);
    var json = new JsonFileService();
    var log = new LogService(paths);
    var settings = new SettingsService(paths, json);
    var repository = new MilitaryRepository(paths, log);
    var service = new FurrielBulletinService(paths, settings, repository, log);
    var military = new FurrielMilitaryOption
    {
        Id = 7,
        Rank = "3º Sgt",
        FullName = "JOÃO PEDRO DA SILVA",
        WarName = "SILVA",
        Cpf = "123.456.789-09",
        Source = FurrielBulletinService.SourceActive
    };
    var store = new FurrielIndexStore
    {
        UpdatedAt = "2026-09-05 08:00:00",
        Files =
        [
            new FurrielBulletinFile
            {
                Id = "ADT-001",
                Bulletin = "1/2026",
                Date = "05/09/2026",
                OriginalName = "ADT_FURRIEL_001_2026.pdf",
                StoredPath = Path.Combine(temporaryDirectory, "ADT_FURRIEL_001_2026.pdf"),
                IndexedAt = "2026-09-05 08:01:00",
                Lines =
                [
                    new FurrielIndexedLine { Page = 3, Text = "a. AUXÍLIO-TRANSPORTE - Implantação", Normalized = "a auxilio transporte implantacao", Subject = "e", Major = "Pagamento Pessoal" },
                    new FurrielIndexedLine { Page = 3, Text = "Seja realizada a implantação em favor do militar abaixo.", Normalized = "seja realizada a implantacao em favor do militar abaixo", Subject = "e", Major = "Pagamento Pessoal" },
                    new FurrielIndexedLine { Page = 3, Text = "3º Sgt JOÃO PEDRO DA SILVA", Normalized = "3 sgt joao pedro da silva", Subject = "e", Major = "Pagamento Pessoal" }
                ],
                Mentions =
                [
                    new BulletinMentionItem
                    {
                        MentionedMilitaryName = military.FullName,
                        MentionedMilitaryWarName = military.WarName,
                        MentionedMilitaryRank = military.Rank,
                        MentionedMilitaryCpf = military.Cpf,
                        IsDatabaseMatch = true,
                        Subject = "e",
                        NoteTitle = string.Empty,
                        SubjectNoteDisplay = "e",
                        NoteText = "a. AUXÍLIO-TRANSPORTE - Implantação\nSeja realizada a implantação para JOÃO PEDRO DA SILVA.",
                        NoteExcerpt = "JOÃO PEDRO DA SILVA - implantação.",
                        PageNumber = 3
                    }
                ]
            }
        ]
    };

    await service.SaveIndexAsync(store);
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.WarmSearchIndexAsync(store)));
    var cachePath = service.SearchIndexFile;
    Check(File.Exists(cachePath), "o aquecimento paralelo deve criar um único cache persistente");

    using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath)))
    {
        var signature = document.RootElement.GetProperty("Signature").GetString() ?? string.Empty;
        var rows = document.RootElement.GetProperty("Rows").GetArrayLength();
        Check(signature.Length == 64 && signature.All(Uri.IsHexDigit), "a assinatura deve ser SHA-256 estável");
        Check(rows > 0, "o cache deve conter linhas pesquisáveis");
    }

    var firstWrite = File.GetLastWriteTimeUtc(cachePath);
    await Task.Delay(1100);
    SimulateNewProcess();
    var secondService = new FurrielBulletinService(paths, settings, repository, log);
    await secondService.WarmSearchIndexAsync(store);
    Check(File.GetLastWriteTimeUtc(cachePath) == firstWrite, "uma nova execução deve reutilizar o cache sem reconstruí-lo");

    var results = secondService.Search(store, string.Empty, military, null, string.Empty);
    Check(results.Count == 1, $"a busca pelo militar deve retornar sua nota salva (retornou {results.Count}: {string.Join(" | ", results.Select(x => $"p{x.Page} [{x.Subject}] [{x.SubjectNoteDisplay}]"))})");
    Check(results[0].Page == 3 && results[0].SubjectNoteDisplay == "Auxílio-Transporte - Implantação",
        "a nota deve usar o título estrutural completo do ADT, nunca o fragmento salvo");
    Check(FurrielBulletinService.ResolveProfessionalSubjectNoteDisplay(
              "a. ADICIONAL FÉRIAS - Ordem de Saque (Continuação do Adt Furr Nr 22 BAR 29, de 16/07/2025) Pag nº 2")
          == "Adicional Férias - Ordem de Saque",
        "cabeçalho de continuação e página não podem contaminar o título da nota");

    var timer = Stopwatch.StartNew();
    var missing = secondService.Search(store, "NOME INEXISTENTE", null, null, string.Empty);
    timer.Stop();
    Check(missing.Count == 0, "nome inexistente deve retornar vazio");
    Check(timer.Elapsed < TimeSpan.FromSeconds(1), "resultado vazio não pode disparar varredura lenta dos ADTs");

    Console.WriteLine("ADT Furriel: 10 verificações de cache, pesquisa e assunto/nota aprovadas.");
}
finally
{
    if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
}
