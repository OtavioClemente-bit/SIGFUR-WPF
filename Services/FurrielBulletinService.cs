using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed partial class FurrielBulletinService
{
    public const int SchemaVersion = 11;
    private const int CurrentParserVersion = 26;
    public const string SourceActive = "Ativos";
    public const string SourceAll = "Ativos + licenciados/transferidos";
    public const string SourceInactive = "Só licenciados/transferidos";

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions PersistentIndexJsonOptions = new(JsonOptions)
    {
        WriteIndented = false
    };

    private static readonly string[] MonthNames =
    [
        "Janeiro", "Fevereiro", "Março", "Abril", "Maio", "Junho",
        "Julho", "Agosto", "Setembro", "Outubro", "Novembro", "Dezembro"
    ];

    private static readonly Dictionary<string, int> MonthLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        ["janeiro"] = 1, ["jan"] = 1,
        ["fevereiro"] = 2, ["fev"] = 2,
        ["marco"] = 3, ["março"] = 3, ["mar"] = 3,
        ["abril"] = 4, ["abr"] = 4,
        ["maio"] = 5, ["mai"] = 5,
        ["junho"] = 6, ["jun"] = 6,
        ["julho"] = 7, ["jul"] = 7,
        ["agosto"] = 8, ["ago"] = 8,
        ["setembro"] = 9, ["set"] = 9,
        ["outubro"] = 10, ["out"] = 10,
        ["novembro"] = 11, ["nov"] = 11,
        ["dezembro"] = 12, ["dez"] = 12
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    { "de", "da", "do", "das", "dos", "e", "a", "o", "ao", "aos" };

    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly MilitaryRepository _repository;
    private readonly LogService _log;

    private static readonly object SearchCacheLock = new();
    private static readonly Dictionary<string, List<FurrielSearchResult>> SearchCache = new(StringComparer.OrdinalIgnoreCase);
    private static string SearchCacheSignature = string.Empty;
    private const int SearchCacheMaxEntries = 250;
    private static readonly object PersistentIndexLock = new();
    private static FurrielPersistentSearchIndex? PersistentIndexMemory;
    private static string PersistentIndexMemoryPath = string.Empty;

    public FurrielBulletinService(AppPaths paths, SettingsService settings, MilitaryRepository repository, LogService log)
    {
        _paths = paths;
        _settings = settings;
        _repository = repository;
        _log = log;
        Directory.CreateDirectory(ModuleDirectory);
        Directory.CreateDirectory(PdfDirectory);
        Directory.CreateDirectory(SignedDirectory);
        Directory.CreateDirectory(TempDirectory);
    }

    public string ModuleDirectory => Path.Combine(_paths.DataDirectory, "boletim_furriel");
    public string PdfDirectory => Path.Combine(ModuleDirectory, "pdfs");
    public string SignedDirectory => Path.Combine(ModuleDirectory, "assinados");
    public string TempDirectory => Path.Combine(ModuleDirectory, "tmp");
    public string IndexFile => Path.Combine(ModuleDirectory, "indice_furriel.json");
    public string SettingsFile => Path.Combine(ModuleDirectory, "cfg.json");
    public string SearchIndexFile => Path.Combine(ModuleDirectory, "pesquisa_furriel_cache.json");

    public IReadOnlyList<string> PersonnelSources => [SourceActive, SourceAll, SourceInactive];
    public IReadOnlyList<string> MonthFilterLabels => ["Todos", .. MonthNames];

    public async Task<FurrielIndexStore> LoadIndexAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(IndexFile)) return NewIndex();
            FurrielIndexStore store;
            await using (var stream = File.OpenRead(IndexFile))
                store = await JsonSerializer.DeserializeAsync<FurrielIndexStore>(stream, JsonOptions, cancellationToken) ?? NewIndex();
            var previousSchema = store.SchemaVersion;
            if (previousSchema < SchemaVersion)
                NormalizeIndex(store);
            else
                PrepareCurrentIndex(store);
            var renamedSignedFiles = NormalizeSignedFileNames(store);
            if (previousSchema < SchemaVersion || renamedSignedFiles)
                await SaveIndexAsync(store, cancellationToken);
            return store;
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao carregar o índice do Aditamento Furriel.", ex);
            return NewIndex();
        }
    }

    public async Task SaveIndexAsync(FurrielIndexStore store, CancellationToken cancellationToken = default)
    {
        NormalizeIndex(store);
        store.SchemaVersion = SchemaVersion;
        store.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", PtBr);
        Directory.CreateDirectory(ModuleDirectory);
        var temp = IndexFile + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken);
        File.Move(temp, IndexFile, true);
        ClearSearchCache();
        await RebuildPersistentSearchIndexAsync(store, cancellationToken);
    }

    public async Task<FurrielModuleSettings> LoadModuleSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new FurrielModuleSettings();
            await using var stream = File.OpenRead(SettingsFile);
            return await JsonSerializer.DeserializeAsync<FurrielModuleSettings>(stream, JsonOptions, cancellationToken)
                   ?? new FurrielModuleSettings();
        }
        catch { return new FurrielModuleSettings(); }
    }

    public async Task SaveModuleSettingsAsync(FurrielModuleSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ModuleDirectory);
        var temp = SettingsFile + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        File.Move(temp, SettingsFile, true);
    }

    public async Task<List<FurrielMilitaryOption>> LoadMilitaryOptionsAsync(string source, CancellationToken cancellationToken = default)
    {
        source = NormalizePersonnelSource(source);
        var result = new List<FurrielMilitaryOption>();
        if (source is SourceActive or SourceAll)
        {
            var active = await _repository.GetAllAsync(cancellationToken);
            result.AddRange(active.Select(x => new FurrielMilitaryOption
            {
                Id = x.Id,
                Rank = x.Rank,
                FullName = x.Name,
                WarName = x.WarName,
                Cpf = x.Cpf,
                Identity = x.MilitaryId,
                PrecCp = x.PrecCp,
                Source = SourceActive
            }));
        }

        if (source is SourceInactive or SourceAll)
            result.AddRange(await LoadInactiveMilitaryAsync(cancellationToken));

        return result
            .Where(x => !string.IsNullOrWhiteSpace(x.FullName) || !string.IsNullOrWhiteSpace(x.WarName))
            .GroupBy(x => BuildMilitaryIdentity(x), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => MilitaryRankService.GetOrder(x.Rank))
            .ThenBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public FurrielMilitaryOption? FindBestMilitary(string text, IEnumerable<FurrielMilitaryOption> military)
    {
        var query = Normalize(text);
        var queryDigits = Digits(text);
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(queryDigits)) return null;

        return military
            .Select((item, index) => (Item: item, Score: ScoreMilitary(item, query, queryDigits), Index: index))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Item)
            .FirstOrDefault();
    }

    public async Task<FurrielImportSummary> ImportAsync(
        FurrielIndexStore store,
        IEnumerable<string> sources,
        bool forceSigned,
        FurrielBulletinFile? selectedHint,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var summary = new FurrielImportSummary();
        var expanded = await ExpandSourcesAsync(sources, summary.Errors, cancellationToken);
        if (expanded.Count == 0) return summary;

        var common = forceSigned ? new List<string>() : expanded.Where(x => !IsSignedByName(x)).ToList();
        var signed = forceSigned ? expanded : expanded.Where(IsSignedByName).ToList();
        var byId = store.Files.Where(x => !string.IsNullOrWhiteSpace(x.Id)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var done = 0;
        var total = common.Count + signed.Count;

        foreach (var path in common)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Indexando PDF {++done}/{total}: {Path.GetFileName(path)}");
            try
            {
                var sourceHash = HashFile(path);
                if (byId.TryGetValue(sourceHash, out var cached) && cached.ParserVersion >= CurrentParserVersion && File.Exists(cached.StoredPath))
                    continue;
                var (created, item) = await RegisterCommonAsync(store, path, byId, cancellationToken);
                if (created) summary.CommonNew++; else summary.CommonUpdated++;
                byId[item.Id] = item;
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                await _log.WriteAsync($"Falha ao indexar Aditamento Furriel: {path}", ex);
            }
        }

        foreach (var path in signed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Vinculando assinado {++done}/{total}: {Path.GetFileName(path)}");
            try
            {
                var hint = signed.Count == 1 ? selectedHint : null;
                var created = await RegisterSignedAsync(store, path, hint, cancellationToken);
                if (created) summary.SignedNew++; else summary.SignedUpdated++;
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                await _log.WriteAsync($"Falha ao vincular Aditamento Furriel assinado: {path}", ex);
            }
        }

        store.Files = store.Files.OrderBy(FileOrderKey).ToList();
        await SaveIndexAsync(store, cancellationToken);
        CleanupTempFolders();
        return summary;
    }

    public async Task<int> ImportSubjectIndexAsync(FurrielIndexStore store, string pdfPath, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            throw new FileNotFoundException("PDF do Índice por Assunto do Furriel não encontrado.", pdfPath);

        progress?.Report("Lendo Índice por Assunto do Aditamento Furriel...");
        var hash = HashFile(pdfPath);
        var pages = await App.PdfText.ExtractPagesAsync(pdfPath, cancellationToken);
        var entries = ParseFurrielSubjectIndex(pages, Path.GetFullPath(pdfPath), hash);
        if (entries.Count == 0)
            throw new InvalidOperationException("Nenhum registro de Adt Furr/Nota foi identificado no Indice por Assunto. Confira se o PDF foi gerado em 'Indice por Assunto' para codTipoBol=3.");

        // O índice por assunto do ADT Furriel é a fonte oficial de assunto/nota.
        // Ele não usa a linha de categoria do SisBol, como “PAGAMENTO PESSOAL”.
        store.SubjectIndex = entries
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => ParseDate(x.BulletinDate) ?? DateTime.MaxValue)
            .ThenBy(x => BulletinNumber(x.BulletinNumber) ?? int.MaxValue)
            .ThenBy(x => x.Page)
            .ThenBy(x => x.NoteNumber)
            .ToList();

        foreach (var file in store.Files)
            NormalizeLegacyFurrielMentions(file, store.SubjectIndex);

        await SaveIndexAsync(store, cancellationToken);
        return store.SubjectIndex.Count;
    }

    public async Task<List<string>> ReindexSavedPdfsOnlyAsync(FurrielIndexStore store, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var pdfs = Directory.Exists(PdfDirectory)
            ? Directory.EnumerateFiles(PdfDirectory, "*.pdf", SearchOption.AllDirectories)
                .Where(x => IsInsideDirectory(x, PdfDirectory))
                .OrderBy(x => Path.GetFileName(x), StringComparer.CurrentCultureIgnoreCase)
                .ToList()
            : [];

        var result = new List<FurrielBulletinFile>();
        var total = pdfs.Count;
        for (var i = 0; i < pdfs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pdfs[i];
            progress?.Report($"Reindexando PDF salvo {i + 1}/{total}: {Path.GetFileName(path)}");
            try
            {
                var indexed = await IndexPdfAsync(path, cancellationToken);
                if (!IsFurrielBulletinPdf(indexed))
                {
                    errors.Add($"Ignorado por não ser Aditamento do Furriel: {Path.GetFileName(path)}");
                    continue;
                }
                indexed.SourcePath = string.IsNullOrWhiteSpace(indexed.SourcePath) ? Path.GetFullPath(path) : indexed.SourcePath;
                indexed.SourceDirectory = string.IsNullOrWhiteSpace(indexed.SourceDirectory) ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty : indexed.SourceDirectory;
                indexed.SourceOriginalName = string.IsNullOrWhiteSpace(indexed.SourceOriginalName) ? Path.GetFileName(path) : indexed.SourceOriginalName;
                indexed.StoredPath = Path.GetFullPath(path);
                foreach (var mention in indexed.Mentions) mention.SourceFilePath = indexed.StoredPath;
                NormalizeLegacyFurrielMentions(indexed, store.SubjectIndex);
                result.Add(indexed);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        store.Files = result.OrderBy(FileOrderKey).ToList();
        await SaveIndexAsync(store, cancellationToken);
        return errors;
    }

    public async Task<List<string>> ReindexAllAsync(FurrielIndexStore store, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        return await ReindexSavedPdfsOnlyAsync(store, progress, cancellationToken);
    }

    public async Task<List<string>> ReindexFilesAsync(
        FurrielIndexStore store,
        IEnumerable<FurrielBulletinFile> selectedFiles,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var selected = selectedFiles
            .Where(x => x is not null)
            .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = selected[i];
            progress?.Report($"Reindexando ADT {i + 1}/{selected.Count}: {current.OriginalName}");
            if (string.IsNullOrWhiteSpace(current.StoredPath) || !File.Exists(current.StoredPath))
            {
                errors.Add($"Arquivo não encontrado: {current.OriginalName}");
                continue;
            }

            try
            {
                var indexed = await IndexPdfAsync(current.StoredPath, cancellationToken);
                if (!IsFurrielBulletinPdf(indexed))
                {
                    errors.Add($"Ignorado por não ser Aditamento Furriel: {current.OriginalName}");
                    continue;
                }

                indexed.SourcePath = current.SourcePath;
                indexed.SourceDirectory = current.SourceDirectory;
                indexed.SourceOriginalName = current.SourceOriginalName;
                indexed.StoredPath = current.StoredPath;
                foreach (var mention in indexed.Mentions) mention.SourceFilePath = indexed.StoredPath;
                NormalizeLegacyFurrielMentions(indexed, store.SubjectIndex);

                var position = store.Files.FindIndex(x => x.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase));
                if (position >= 0) store.Files[position] = indexed;
                else store.Files.Add(indexed);
            }
            catch (Exception ex)
            {
                errors.Add($"{current.OriginalName}: {ex.Message}");
                await _log.WriteAsync($"Falha ao reindexar Aditamento Furriel: {current.StoredPath}", ex);
            }
        }

        store.Files = store.Files.OrderBy(FileOrderKey).ToList();
        await SaveIndexAsync(store, cancellationToken);
        return errors;
    }

    public List<FurrielBulletinFile> FilterFiles(FurrielIndexStore store, FurrielPeriodFilter? period, string bulletinFilter)
    {
        foreach (var file in store.Files)
            file.SignedStatus = GetSignedPath(store, file).Length > 0 ? "SIM" : "NÃO";

        return store.Files
            .Where(x => MatchesPeriod(x, period) && MatchesBulletinFilter(x, bulletinFilter))
            .OrderBy(FileOrderKey)
            .ToList();
    }

    public List<FurrielSearchResult> Search(
        FurrielIndexStore store,
        string query,
        FurrielMilitaryOption? military,
        FurrielPeriodFilter? period,
        string bulletinFilter,
        string consequenceFilter = "Todos",
        int limit = 500)
    {
        if (store.Files.Count == 0) return [];

        var signature = BuildSearchCacheSignature(store);
        var cacheKey = BuildSearchCacheKey(signature, query, military, period, bulletinFilter, consequenceFilter, limit);
        if (TryGetSearchCache(signature, cacheKey, out var cached)) return cached;

        // Caminho rápido: usa o índice persistente já montado no download/reindexação.
        // Assim a tela não precisa varrer e reinterpretar todos os PDFs a cada nome pesquisado.
        List<FurrielSearchResult> rows;
        try
        {
            rows = SearchPersistentIndex(store, signature, query, military, period, bulletinFilter, consequenceFilter, limit);
        }
        catch (Exception ex)
        {
            // Não deixa uma falha transitória do cache derrubar a pesquisa nem abrir
            // a mesma caixa de erro que ocorria no primeiro uso do módulo.
            _ = _log.WriteAsync("Falha ao consultar o cache rápido do Aditamento Furriel.", ex);
            rows = [];
        }

        SetSearchCache(signature, cacheKey, rows);
        return rows.ToList();
    }

    public async Task WarmSearchIndexAsync(FurrielIndexStore store, CancellationToken cancellationToken = default)
    {
        if (store.Files.Count == 0) return;
        try
        {
            var signature = BuildSearchCacheSignature(store);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = GetOrCreatePersistentSearchIndex(store, signature);
            }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // O módulo continua utilizável mesmo se o disco estiver temporariamente
            // indisponível. A falha fica no log e não vira a caixa de erro inicial.
            await _log.WriteAsync("Não foi possível preparar o cache rápido do Aditamento Furriel.", ex);
        }
    }


    private List<FurrielSearchResult> SearchPersistentIndex(
        FurrielIndexStore store,
        string signature,
        string query,
        FurrielMilitaryOption? military,
        FurrielPeriodFilter? period,
        string bulletinFilter,
        string consequenceFilter,
        int limit)
    {
        var index = GetOrCreatePersistentSearchIndex(store, signature);

        var profile = BuildSearchProfile(query, military);
        var files = store.Files
            .Where(x => MatchesPeriod(x, period) && MatchesBulletinFilter(x, bulletinFilter))
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FurrielSearchResult>();
        foreach (var row in index.Rows)
        {
            if (!files.ContainsKey(row.FileId)) continue;
            if (consequenceFilter == "Apenas com consequência" && !row.Result.HasConsequence) continue;
            if (consequenceFilter == "Apenas Furriel/Secretaria/Pagamento" && !row.Result.IsFurrielConsequence) continue;

            // Quando o usuário escolhe um militar do cadastro, o cache rápido não pode
            // aprovar uma linha só porque o nome apareceu perto dela no bloco do PDF.
            // Primeiro valida a linha ORIGINAL do índice. Só depois padroniza a exibição.
            // Isso elimina a duplicidade visual "nome" + "Prec/CPF" da mesma nota.
            if (profile.FromDatabase && !MatchesDirectMilitaryResult(row.Result, profile)) continue;
            if (!MatchesProfile(row.SearchNormalized, row.SearchDigits, profile)) continue;

            var item = profile.FromDatabase
                ? CanonicalizeSelectedMilitaryResult(row.Result, military)
                : CloneSearchResult(row.Result);
            var key = FurrielSearchResultLogicalKey(item, profile.FromDatabase ? military : null);
            if (!seen.Add(key)) continue;
            result.Add(item);
            if (result.Count >= limit) break;
        }

        return SortResults(result);
    }

    private FurrielPersistentSearchIndex GetOrCreatePersistentSearchIndex(FurrielIndexStore store, string signature)
    {
        lock (PersistentIndexLock)
        {
            var fullPath = Path.GetFullPath(SearchIndexFile);
            if (PersistentIndexMemory is not null &&
                PersistentIndexMemoryPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) &&
                PersistentIndexMemory.Signature.Equals(signature, StringComparison.OrdinalIgnoreCase))
                return PersistentIndexMemory;

            var index = LoadPersistentSearchIndexUnsafe(signature);
            if (index is null)
            {
                index = BuildPersistentSearchIndex(store, signature);
                // A pesquisa já pode usar o índice em memória mesmo se outro processo
                // estiver substituindo o arquivo persistente neste exato momento.
                PersistentIndexMemory = index;
                PersistentIndexMemoryPath = fullPath;
                TrySavePersistentSearchIndexUnsafe(index);
                return index;
            }

            PersistentIndexMemory = index;
            PersistentIndexMemoryPath = fullPath;
            return index;
        }
    }

    private FurrielPersistentSearchIndex? LoadPersistentSearchIndexUnsafe(string signature)
    {
        try
        {
            if (!File.Exists(SearchIndexFile)) return null;
            using var stream = new FileStream(
                SearchIndexFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var index = JsonSerializer.Deserialize<FurrielPersistentSearchIndex>(stream, PersistentIndexJsonOptions);
            return index is not null && index.Signature.Equals(signature, StringComparison.OrdinalIgnoreCase)
                ? index
                : null;
        }
        catch { return null; }
    }

    private async Task RebuildPersistentSearchIndexAsync(FurrielIndexStore store, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = BuildSearchCacheSignature(store);
            lock (PersistentIndexLock)
            {
                var index = BuildPersistentSearchIndex(store, signature);
                PersistentIndexMemory = index;
                PersistentIndexMemoryPath = Path.GetFullPath(SearchIndexFile);
                TrySavePersistentSearchIndexUnsafe(index);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { await _log.WriteAsync("Falha ao montar cache rápido do Aditamento Furriel.", ex); }
    }

    private void TrySavePersistentSearchIndexUnsafe(FurrielPersistentSearchIndex index)
    {
        var temp = SearchIndexFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(ModuleDirectory);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, index, PersistentIndexJsonOptions);

            // O temporário exclusivo evita colisão entre pesquisas simultâneas. As
            // tentativas cobrem também uma segunda instância do SIGFUR lendo o cache.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, SearchIndexFile, true);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(40 * (attempt + 1));
                }
            }
        }
        catch (Exception ex)
        {
            _ = _log.WriteAsync("Não foi possível persistir o cache rápido do Aditamento Furriel.", ex);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { }
        }
    }

    private FurrielPersistentSearchIndex BuildPersistentSearchIndex(FurrielIndexStore store, string signature)
    {
        var index = new FurrielPersistentSearchIndex
        {
            Signature = signature,
            BuiltAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", PtBr)
        };

        foreach (var file in store.Files)
        {
            var canonicalBulletin = CanonicalFurrielBulletin(file.Bulletin, file.Date, file.OriginalName, file.StoredPath);
            var signedPath = GetSignedPath(store, file);

            foreach (var mention in file.Mentions ?? [])
            {
                var text = OneLine(string.Join(' ', mention.DisplayMilitary, mention.MentionedMilitaryName, mention.MentionedMilitaryWarName, mention.MentionedMilitaryCpf, mention.MentionedMilitaryPrecCp, mention.NoteText, mention.NoteExcerpt, mention.ConsequenceText, mention.Subject, mention.NoteTitle, mention.SubjectNoteDisplay));
                if (string.IsNullOrWhiteSpace(text)) continue;
                var subject = CleanDisplaySubject(FirstNonEmpty(mention.Subject, "Menção no ADT Furriel"));
                var note = CleanDisplayNote(FirstNonEmpty(mention.NoteTitle, mention.SubjectNoteDisplay, "Menção"), subject);
                var display = ComposeSubjectNote(subject, note, mention.SubjectNoteDisplay);
                AddPersistentRow(index, file.Id, text, new FurrielSearchResult
                {
                    Military = mention.DisplayMilitary,
                    FullName = mention.MentionedMilitaryName,
                    WarName = mention.MentionedMilitaryWarName,
                    PdfSearchTerm = string.IsNullOrWhiteSpace(mention.MentionedMilitaryName) ? subject : mention.MentionedMilitaryName,
                    MatchFromDatabase = mention.IsDatabaseMatch,
                    Type = mention.IsConsequenceMention ? "Menção na consequência" : mention.HasConsequence ? "Nota com consequência" : "Menção",
                    Bulletin = canonicalBulletin, Bar = file.Bar, Date = file.Date, Page = mention.PageNumber ?? 1,
                    Subject = subject, SubjectNoteDisplay = display, Preview = Truncate(FirstNonEmpty(mention.NoteExcerpt, text), 260),
                    Context = Truncate(FirstNonEmpty(mention.NoteText, text), 1100), NoteText = Truncate(FirstNonEmpty(mention.NoteText, text), 1100),
                    HasConsequence = mention.HasConsequence, IsFurrielConsequence = mention.IsFurrielConsequence, ConsequenceText = mention.ConsequenceText,
                    FileName = file.OriginalName, PdfPath = file.StoredPath, SignedPdfPath = signedPath, Signed = string.IsNullOrWhiteSpace(signedPath) ? "NÃO" : "SIM",
                    SignedFileName = Path.GetFileName(signedPath), IndexedAt = file.IndexedAt
                });
            }

            var lines = file.Lines ?? [];
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line.Text) || IsStructuralLine(line.Text)) continue;
                var start = Math.Max(0, i - 8);
                var end = Math.Min(lines.Count - 1, i + 16);
                var segment = lines.Skip(start).Take(end - start + 1).ToList();
                var segmentText = OneLine(string.Join(' ', CleanNoteRows(segment)));
                if (string.IsNullOrWhiteSpace(segmentText)) continue;
                var consequence = ExtractOfficialSegmentConsequence(segmentText);
                // A linha indexada pode carregar um fragmento herdado do PDF ("a", "e",
                // "saque" etc.). O bloco ao redor da ocorrência contém o cabeçalho real
                // da nota e deve ser a fonte principal também no cache persistente.
                var titleFromText = ResolveFurrielTitleNear(lines, i);
                if (string.IsNullOrWhiteSpace(titleFromText.Subject))
                    titleFromText = ResolveFurrielTitleFromText(segmentText);
                var subject = CleanDisplaySubject(FirstNonEmpty(titleFromText.Subject, line.Subject));
                var note = CleanDisplayNote(FirstNonEmpty(titleFromText.Note, line.Major), subject);
                if (!IsTrustworthyFurrielTitle(subject, note))
                {
                    subject = "Menção no ADT Furriel";
                    note = string.Empty;
                }
                var display = ComposeSubjectNote(subject, note, string.Empty);
                AddPersistentRow(index, file.Id, segmentText, new FurrielSearchResult
                {
                    Military = Truncate(line.Text, 110), FullName = string.Empty, WarName = string.Empty, PdfSearchTerm = line.Text,
                    MatchFromDatabase = false, Type = consequence.HasConsequence ? "Nota com consequência" : "Menção",
                    Bulletin = canonicalBulletin, Bar = file.Bar, Date = file.Date, Page = Math.Max(1, line.Page),
                    Subject = subject, SubjectNoteDisplay = display, Preview = Truncate(segmentText, 260), Context = Truncate(segmentText, 1100), NoteText = Truncate(segmentText, 1100),
                    HasConsequence = consequence.HasConsequence, IsFurrielConsequence = consequence.IsFurrielConsequence, ConsequenceText = consequence.Text,
                    FileName = file.OriginalName, PdfPath = file.StoredPath, SignedPdfPath = signedPath, Signed = string.IsNullOrWhiteSpace(signedPath) ? "NÃO" : "SIM",
                    SignedFileName = Path.GetFileName(signedPath), IndexedAt = file.IndexedAt
                });
            }
        }

        return index;
    }

    private static void AddPersistentRow(FurrielPersistentSearchIndex index, string fileId, string searchText, FurrielSearchResult result)
    {
        var normalized = Normalize(searchText);
        if (normalized.Length < 3 && Digits(searchText).Length < 5) return;
        index.Rows.Add(new FurrielPersistentSearchRow
        {
            FileId = fileId ?? string.Empty,
            SearchNormalized = normalized,
            SearchDigits = Digits(searchText),
            Result = result
        });
    }

    public void ClearSearchCache()
    {
        lock (SearchCacheLock)
        {
            SearchCache.Clear();
            SearchCacheSignature = string.Empty;
        }
    }

    private static bool TryGetSearchCache(string signature, string key, out List<FurrielSearchResult> rows)
    {
        lock (SearchCacheLock)
        {
            if (!SearchCacheSignature.Equals(signature, StringComparison.OrdinalIgnoreCase))
            {
                SearchCache.Clear();
                SearchCacheSignature = signature;
            }

            if (SearchCache.TryGetValue(key, out var cached))
            {
                rows = cached.ToList();
                return true;
            }
        }

        rows = [];
        return false;
    }

    private static void SetSearchCache(string signature, string key, List<FurrielSearchResult> rows)
    {
        lock (SearchCacheLock)
        {
            if (!SearchCacheSignature.Equals(signature, StringComparison.OrdinalIgnoreCase))
            {
                SearchCache.Clear();
                SearchCacheSignature = signature;
            }

            if (SearchCache.Count >= SearchCacheMaxEntries) SearchCache.Clear();
            SearchCache[key] = rows.ToList();
        }
    }

    private static string BuildSearchCacheSignature(FurrielIndexStore store)
    {
        var content = new StringBuilder();
        AppendSignatureToken(content, "FURRIEL_SEARCH_CACHE_V2");
        AppendSignatureToken(content, CurrentParserVersion.ToString(CultureInfo.InvariantCulture));
        AppendSignatureToken(content, store.UpdatedAt);
        AppendSignatureToken(content, store.Files.Count.ToString(CultureInfo.InvariantCulture));
        AppendSignatureToken(content, store.SubjectIndex.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var file in store.Files)
        {
            AppendSignatureToken(content, file.Id);
            AppendSignatureToken(content, file.IndexedAt);
            AppendSignatureToken(content, (file.Mentions?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
            AppendSignatureToken(content, (file.Lines?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
    }

    private static void AppendSignatureToken(StringBuilder destination, string? value)
    {
        value ??= string.Empty;
        destination.Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static string BuildSearchCacheKey(string signature, string query, FurrielMilitaryOption? military, FurrielPeriodFilter? period, string bulletinFilter, string consequenceFilter, int limit)
    {
        var militaryKey = military is null
            ? string.Empty
            : string.Join('§', military.Id, Normalize(military.FullName), Normalize(military.WarName), Digits(military.Cpf), Digits(military.PrecCp), Digits(military.Identity));
        return string.Join('|',
            signature,
            Normalize(query),
            militaryKey,
            Normalize(period?.Month ?? string.Empty),
            Normalize(period?.Year ?? string.Empty),
            period?.Start?.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            period?.End?.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty,
            Normalize(bulletinFilter),
            Normalize(consequenceFilter),
            limit.ToString(CultureInfo.InvariantCulture));
    }

    private List<FurrielSearchResult> SearchProfessionalMentions(
        FurrielIndexStore store,
        string query,
        FurrielMilitaryOption? military,
        FurrielPeriodFilter? period,
        string bulletinFilter,
        string consequenceFilter,
        int limit)
    {
        var normalizedQuery = Normalize(query);
        var queryDigits = Digits(query);
        var selectedName = Normalize(military?.FullName ?? string.Empty);
        var selectedWar = Normalize(military?.WarName ?? string.Empty);
        var selectedDigits = new[] { military?.Cpf, military?.PrecCp, military?.Identity }.Select(Digits).Where(x => x.Length >= 5).ToList();
        var rows = new List<FurrielSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in store.Files.Where(x => MatchesPeriod(x, period) && MatchesBulletinFilter(x, bulletinFilter)))
        {
            foreach (var mention in file.Mentions)
            {
                if (consequenceFilter == "Apenas com consequência" && !mention.HasConsequence) continue;
                if (consequenceFilter == "Apenas Furriel/Secretaria/Pagamento" && !mention.IsFurrielConsequence) continue;
                var rawEvidenceText = string.Join(' ', file.Bulletin, file.Date, file.Bar, mention.BulletinType, mention.BulletinNumber,
                    mention.Subject, mention.NoteTitle, mention.NoteNumber, mention.SubjectNoteDisplay, mention.NoteText, mention.NoteExcerpt, mention.ConsequenceText);
                var searchableText = string.Join(' ', rawEvidenceText,
                    mention.MentionedMilitaryName, mention.MentionedMilitaryWarName, mention.MentionedMilitaryCpf, mention.MentionedMilitaryPrecCp);
                var normalizedRawEvidence = Normalize(rawEvidenceText);
                var normalized = Normalize(searchableText);
                var digits = Digits(searchableText);

                // Regra profissional: quando o usuário escolhe um militar, a busca deve ser EXATA
                // no texto real da menção. Não aceitar MilitaryId/nome de guerra quando o nome ou
                // documento do militar selecionado não aparece no trecho extraído do PDF.
                var selectedMatch = military is null || IsExactSelectedMilitaryMention(mention, military, normalizedRawEvidence, Digits(rawEvidenceText));
                var freeMatch = normalizedQuery.Length == 0 || normalized.Contains(normalizedQuery, StringComparison.Ordinal) ||
                    queryDigits.Length >= 5 && digits.Contains(queryDigits, StringComparison.Ordinal);
                if (!selectedMatch || !freeMatch) continue;
                var signedPath = GetSignedPath(store, file);
                var profile = BuildSearchProfile(string.Empty, military ?? new FurrielMilitaryOption
                {
                    Id = mention.MilitaryId ?? 0,
                    Rank = mention.MentionedMilitaryRank,
                    FullName = mention.MentionedMilitaryName,
                    WarName = mention.MentionedMilitaryWarName,
                    Cpf = mention.MentionedMilitaryCpf,
                    PrecCp = mention.MentionedMilitaryPrecCp,
                    Source = "Índice"
                });
                var improved = ResolveProfessionalMentionContext(file.Lines ?? [], mention, profile);
                var officialIndex = FindOfficialSubjectIndexByNoteNumber(file, mention, store.SubjectIndex)
                    ?? FindOfficialSubjectIndexByLetterAnchor(file, mention, file.Lines ?? [], store.SubjectIndex)
                    ?? FindBestSubjectIndexEntry(store.SubjectIndex, file, mention, FirstNonEmpty(improved.Context, mention.NoteText, mention.NoteExcerpt));
                if (officialIndex is not null && !IsTrustworthySubjectIndexEntry(officialIndex)) officialIndex = null;

                // O próprio cabeçalho da nota é a fonte principal. O Índice por Assunto só
                // complementa quando contém um título estrutural confiável para a mesma nota.
                var cleanSubject = CleanDisplaySubject(FirstNonEmpty(improved.Subject, officialIndex?.Subject, mention.Subject, "Menção no ADT Furriel"));
                var cleanNote = CleanDisplayNote(FirstNonEmpty(improved.NoteTitle, officialIndex?.NoteType, mention.NoteTitle, "Menção"), cleanSubject);
                if (string.IsNullOrWhiteSpace(cleanSubject)) cleanSubject = "Menção no ADT Furriel";
                if (string.IsNullOrWhiteSpace(cleanNote)) cleanNote = "Menção";
                if (IsGenericFurrielSubject(cleanSubject) && string.IsNullOrWhiteSpace(officialIndex?.Subject)) cleanSubject = "Menção no ADT Furriel";
                var display = ComposeSubjectNote(cleanSubject, cleanNote, string.Empty);
                var canonicalBulletin = CanonicalFurrielBulletin(file.Bulletin, file.Date, file.OriginalName, file.StoredPath);
                var page = improved.Page > 0 ? improved.Page : mention.PageNumber ?? 1;
                // Se o usuário selecionou um militar, cada ADT/página deve aparecer uma vez só.
                // O parser pode capturar a mesma nota em duas linhas (nome completo e depois CPF/PREC),
                // mas para o usuário isso é uma única menção. Mantemos assunto/nota fora da chave
                // nessa situação para eliminar a duplicidade visual.
                var logicalKey = military is not null
                    ? string.Join('|',
                        FurrielMentionPersonKey(mention, military),
                        Normalize(canonicalBulletin),
                        Normalize(file.Bar),
                        page.ToString(PtBr))
                    : string.Join('|',
                        FurrielMentionPersonKey(mention, military),
                        Normalize(canonicalBulletin),
                        Normalize(file.Bar),
                        page.ToString(PtBr),
                        Normalize(cleanSubject),
                        Normalize(cleanNote));
                if (!seen.Add(logicalKey)) continue;

                rows.Add(new FurrielSearchResult
                {
                    Military = military is not null ? military.DisplayLabel : mention.DisplayMilitary,
                    FullName = military is not null ? military.FullName : mention.MentionedMilitaryName,
                    WarName = military is not null ? military.WarName : mention.MentionedMilitaryWarName,
                    PdfSearchTerm = string.IsNullOrWhiteSpace(mention.MentionedMilitaryName) ? cleanSubject : mention.MentionedMilitaryName,
                    MatchFromDatabase = mention.IsDatabaseMatch,
                    Type = mention.IsConsequenceMention ? "Menção na consequência" : mention.HasConsequence ? "Nota com consequência" : "Menção",
                    Bulletin = canonicalBulletin, Bar = file.Bar, Date = file.Date,
                    Page = page, Subject = cleanSubject,
                    SubjectNoteDisplay = display, Preview = FirstNonEmpty(improved.Preview, mention.NoteExcerpt),
                    Context = FirstNonEmpty(improved.Context, mention.NoteText), NoteText = FirstNonEmpty(improved.Context, mention.NoteText),
                    HasConsequence = mention.HasConsequence, IsFurrielConsequence = mention.IsFurrielConsequence,
                    ConsequenceText = mention.ConsequenceText, FileName = file.OriginalName,
                    PdfPath = file.StoredPath, SignedPdfPath = signedPath,
                    Signed = string.IsNullOrWhiteSpace(signedPath) ? "NÃO" : "SIM",
                    SignedFileName = Path.GetFileName(signedPath), IndexedAt = file.IndexedAt
                });
                if (rows.Count >= limit) return SortResults(rows);
            }
        }
        if (store.SubjectIndex.Count > 0 && rows.Count < limit && ShouldSearchOfficialSubjectSegments(query, military, consequenceFilter))
        {
            foreach (var row in SearchOfficialSubjectIndexSegments(store, query, military, period, bulletinFilter, consequenceFilter, limit - rows.Count))
            {
                if (!seen.Add(FurrielSearchResultLogicalKey(row, military))) continue;
                rows.Add(row);
                if (rows.Count >= limit) return SortResults(rows);
            }
        }
        return SortResults(rows);
    }

    private IEnumerable<FurrielSearchResult> SearchOfficialSubjectIndexSegments(
        FurrielIndexStore store,
        string query,
        FurrielMilitaryOption? military,
        FurrielPeriodFilter? period,
        string bulletinFilter,
        string consequenceFilter,
        int limit)
    {
        if (limit <= 0) yield break;
        var profile = BuildSearchProfile(query, military);
        var yielded = 0;

        foreach (var file in store.Files.Where(x => MatchesPeriod(x, period) && MatchesBulletinFilter(x, bulletinFilter)))
        {
            var lines = file.Lines ?? [];
            if (lines.Count == 0) continue;

            var candidates = OfficialSubjectIndexCandidates(store.SubjectIndex, file)
                .OrderBy(x => x.Page <= 0 ? int.MaxValue : x.Page)
                .ThenBy(x => x.NoteNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count == 0) continue;

            var canonicalBulletin = CanonicalFurrielBulletin(file.Bulletin, file.Date, file.OriginalName, file.StoredPath);
            var signedPath = GetSignedPath(store, file);

            foreach (var entry in candidates)
            {
                var cleanSubject = CleanDisplaySubject(entry.Subject);
                var cleanNote = CleanDisplayNote(entry.NoteType, cleanSubject);
                var display = ComposeSubjectNote(cleanSubject, cleanNote, entry.SubjectNoteDisplay);
                if (IsGenericFurrielSubject(cleanSubject) || IsForbiddenFurrielFreeTextTitle(display)) continue;

                var segment = ResolveOfficialSubjectSegment(lines, candidates, entry);
                if (segment.Count == 0) continue;

                var segmentText = OneLine(string.Join(' ', CleanNoteRows(segment)));
                if (string.IsNullOrWhiteSpace(segmentText))
                    segmentText = OneLine(string.Join(' ', segment.Select(x => CleanSpaces(x.Text))));
                if (!OfficialSegmentMatchesSearch(segmentText, military, profile)) continue;

                var consequence = ExtractOfficialSegmentConsequence(segmentText);
                if (consequenceFilter == "Apenas com consequência" && !consequence.HasConsequence) continue;
                if (consequenceFilter == "Apenas Furriel/Secretaria/Pagamento" && !consequence.IsFurrielConsequence) continue;

                var displayMilitary = ExtractOfficialSegmentMilitaryDisplay(segment, query, military, profile);
                var page = entry.Page > 0 ? entry.Page : Math.Max(1, segment[0].Page);

                yield return new FurrielSearchResult
                {
                    Military = displayMilitary,
                    FullName = military?.FullName ?? displayMilitary,
                    WarName = military?.WarName ?? string.Empty,
                    PdfSearchTerm = military is null ? PlainPdfSearchText(FirstNonEmpty(displayMilitary, query, cleanSubject)) : PdfViewerSearchTerm(military.FullName, military.WarName, query),
                    MatchFromDatabase = military is not null,
                    Type = consequence.HasConsequence ? "Nota com consequência" : "Menção",
                    Bulletin = canonicalBulletin,
                    Bar = file.Bar,
                    Date = file.Date,
                    Page = page,
                    Subject = cleanSubject,
                    SubjectNoteDisplay = display,
                    Preview = Truncate(segmentText, 260),
                    Context = Truncate(segmentText, 1100),
                    NoteText = Truncate(segmentText, 1100),
                    HasConsequence = consequence.HasConsequence,
                    IsFurrielConsequence = consequence.IsFurrielConsequence,
                    ConsequenceText = consequence.Text,
                    FileName = file.OriginalName,
                    PdfPath = file.StoredPath,
                    SignedPdfPath = signedPath,
                    Signed = string.IsNullOrWhiteSpace(signedPath) ? "NÃO" : "SIM",
                    SignedFileName = Path.GetFileName(signedPath),
                    IndexedAt = file.IndexedAt
                };

                yielded++;
                if (yielded >= limit) yield break;
            }
        }
    }


    private List<FurrielSearchResult> SearchRawPdfLines(
        FurrielIndexStore store,
        string query,
        FurrielMilitaryOption? military,
        FurrielPeriodFilter? period,
        string bulletinFilter,
        string consequenceFilter,
        int limit)
    {
        var profile = BuildSearchProfile(query, military);
        var rows = new List<FurrielSearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in store.Files.Where(x => MatchesPeriod(x, period) && MatchesBulletinFilter(x, bulletinFilter)))
        {
            var lines = file.Lines ?? [];
            if (lines.Count == 0) continue;
            var canonicalBulletin = CanonicalFurrielBulletin(file.Bulletin, file.Date, file.OriginalName, file.StoredPath);
            var signedPath = GetSignedPath(store, file);

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (!MatchesProfile(line.Normalized, line.Digits, profile)) continue;

                var start = Math.Max(0, i - 10);
                var end = Math.Min(lines.Count - 1, i + 22);
                var segment = lines.Skip(start).Take(end - start + 1).ToList();
                var segmentText = OneLine(string.Join(' ', CleanNoteRows(segment)));
                if (string.IsNullOrWhiteSpace(segmentText))
                    segmentText = OneLine(string.Join(' ', segment.Select(x => CleanSpaces(x.Text))));

                var consequence = ExtractOfficialSegmentConsequence(segmentText);
                if (consequenceFilter == "Apenas com consequência" && !consequence.HasConsequence) continue;
                if (consequenceFilter == "Apenas Furriel/Secretaria/Pagamento" && !consequence.IsFurrielConsequence) continue;

                var mention = new BulletinMentionItem
                {
                    MentionedMilitaryName = military?.FullName ?? ExtractProbableName(segment, query, profile),
                    MentionedMilitaryWarName = military?.WarName ?? string.Empty,
                    MentionedMilitaryRank = military?.Rank ?? string.Empty,
                    MentionedMilitaryCpf = military?.Cpf ?? string.Empty,
                    MentionedMilitaryPrecCp = military?.PrecCp ?? string.Empty,
                    PageNumber = line.Page,
                    NoteText = segmentText,
                    NoteExcerpt = Truncate(segmentText, 260),
                    Subject = FirstNonEmpty(line.Subject, "Menção no ADT Furriel"),
                    NoteTitle = FirstNonEmpty(line.Major, "Menção")
                };
                var officialIndex = FindBestSubjectIndexEntry(store.SubjectIndex, file, mention, segmentText);
                if (officialIndex is not null && !IsTrustworthySubjectIndexEntry(officialIndex)) officialIndex = null;
                var titleFromText = ResolveFurrielTitleNear(lines, i);
                if (string.IsNullOrWhiteSpace(titleFromText.Subject))
                    titleFromText = ResolveFurrielTitleFromText(segmentText);
                var cleanSubject = CleanDisplaySubject(FirstNonEmpty(titleFromText.Subject, officialIndex?.Subject, line.Subject, "Menção no ADT Furriel"));
                var cleanNote = CleanDisplayNote(FirstNonEmpty(titleFromText.Note, officialIndex?.NoteType, line.Major, "Menção"), cleanSubject);
                if (!IsTrustworthyFurrielTitle(cleanSubject, cleanNote))
                {
                    cleanSubject = "Menção no ADT Furriel";
                    cleanNote = string.Empty;
                }
                if (string.IsNullOrWhiteSpace(cleanSubject)) cleanSubject = "Menção no ADT Furriel";
                var display = ComposeSubjectNote(cleanSubject, cleanNote, string.Empty);

                var key = string.Join('|', Normalize(canonicalBulletin), line.Page.ToString(PtBr), Normalize(cleanSubject), Normalize(cleanNote), Normalize(military?.FullName ?? query));
                if (!seen.Add(key)) continue;

                rows.Add(new FurrielSearchResult
                {
                    Military = military?.DisplayLabel ?? FirstNonEmpty(mention.MentionedMilitaryName, query),
                    FullName = military?.FullName ?? mention.MentionedMilitaryName,
                    WarName = military?.WarName ?? string.Empty,
                    PdfSearchTerm = military is null ? PlainPdfSearchText(FirstNonEmpty(query, mention.MentionedMilitaryName)) : PdfViewerSearchTerm(military.FullName, military.WarName, query),
                    MatchFromDatabase = military is not null,
                    Type = consequence.HasConsequence ? "Nota com consequência" : "Menção no PDF",
                    Bulletin = canonicalBulletin,
                    Bar = file.Bar,
                    Date = file.Date,
                    Page = Math.Max(1, line.Page),
                    Subject = cleanSubject,
                    SubjectNoteDisplay = display,
                    Preview = Truncate(segmentText, 260),
                    Context = Truncate(segmentText, 1100),
                    NoteText = Truncate(segmentText, 1100),
                    HasConsequence = consequence.HasConsequence,
                    IsFurrielConsequence = consequence.IsFurrielConsequence,
                    ConsequenceText = consequence.Text,
                    FileName = file.OriginalName,
                    PdfPath = file.StoredPath,
                    SignedPdfPath = signedPath,
                    Signed = string.IsNullOrWhiteSpace(signedPath) ? "NÃO" : "SIM",
                    SignedFileName = Path.GetFileName(signedPath),
                    IndexedAt = file.IndexedAt
                });
                if (rows.Count >= limit) return SortResults(rows);
            }
        }

        return SortResults(rows);
    }

    private static FurrielSubjectIndexEntry? FindBestSubjectIndexEntry(
        IReadOnlyList<FurrielSubjectIndexEntry>? entries,
        FurrielBulletinFile file,
        BulletinMentionItem mention,
        string context)
    {
        if (entries is null || entries.Count == 0) return null;
        var bulletin = CanonicalFurrielBulletin(file.Bulletin, file.Date, file.OriginalName, file.StoredPath);
        var bulletinNumber = BulletinNumber(bulletin);
        var date = ParseDate(file.Date);
        var page = mention.PageNumber ?? 0;
        var noteNumber = FirstNonEmpty(mention.NoteNumber, ExtractSisbolNoteNumber(string.Join(' ', mention.SubjectNoteDisplay, mention.NoteTitle, mention.NoteText, mention.NoteExcerpt)));
        var contextNorm = Normalize(string.Join(' ', context, mention.Subject, mention.NoteTitle, mention.SubjectNoteDisplay, mention.NoteText, mention.NoteExcerpt));

        var candidates = entries
            .Where(x => BulletinNumber(x.BulletinNumber) == bulletinNumber)
            .Where(x => date is null || ParseDate(x.BulletinDate) is not { } entryDate || entryDate.Date == date.Value.Date)
            .ToList();
        if (candidates.Count == 0 && date is not null && IsFurrielBulletinPdf(file))
        {
            candidates = entries
                .Where(x => ParseDate(x.BulletinDate) is { } entryDate && entryDate.Date == date.Value.Date)
                .ToList();
        }
        if (candidates.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(noteNumber))
        {
            var exactByNote = candidates.FirstOrDefault(x => x.NoteNumber.Equals(noteNumber, StringComparison.OrdinalIgnoreCase));
            if (exactByNote is not null) return exactByNote;
        }

        if (page > 0)
        {
            var exactPageCandidates = candidates
                .Where(x => x.Page <= 0 || x.Page == page)
                .ToList();
            if (exactPageCandidates.Count == 0) return null;
            candidates = exactPageCandidates;
        }

        if (candidates.Count == 1) return candidates[0];

        var scored = candidates.Select(entry =>
        {
            var metadataScore = 0;
            if (page > 0 && entry.Page == page) metadataScore += 500;
            if (date is not null && ParseDate(entry.BulletinDate) is { } entryDate && entryDate.Date == date.Value.Date) metadataScore += 260;

            var contentScore = 0;
            var subjectNorm = Normalize(entry.Subject);
            var noteNorm = Normalize(entry.NoteType);
            var displayNorm = Normalize(entry.SubjectNoteDisplay);
            if (displayNorm.Length > 0 && contextNorm.Contains(displayNorm, StringComparison.Ordinal)) contentScore += 800;
            if (subjectNorm.Length > 0 && contextNorm.Contains(subjectNorm, StringComparison.Ordinal)) contentScore += 360;
            if (noteNorm.Length > 0 && contextNorm.Contains(noteNorm, StringComparison.Ordinal)) contentScore += 240;
            contentScore += SubjectIndexTokenScore(subjectNorm, contextNorm, 14);
            contentScore += SubjectIndexTokenScore(noteNorm, contextNorm, 10);

            return (Entry: entry, MetadataScore: metadataScore, ContentScore: contentScore, Score: metadataScore + contentScore);
        })
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Entry.Page)
        .ThenBy(x => x.Entry.NoteNumber, StringComparer.OrdinalIgnoreCase)
        .ToList();

        var best = scored[0];
        var second = scored.Count > 1 ? scored[1] : default;

        // Não escolher uma nota do índice só por ADT/data/página quando há várias notas
        // na mesma página. Precisa haver casamento do título/texto, ou o número da nota.
        if (best.ContentScore >= 300 && (second.Entry is null || best.Score - second.Score >= 80)) return best.Entry;
        if (best.ContentScore >= 800) return best.Entry;
        return null;
    }

    private static FurrielSubjectIndexEntry? FindOfficialSubjectIndexByNoteNumber(
        FurrielBulletinFile file,
        BulletinMentionItem mention,
        IReadOnlyList<FurrielSubjectIndexEntry>? subjectIndex)
    {
        var noteNumber = FirstNonEmpty(mention.NoteNumber, ExtractSisbolNoteNumber(string.Join(' ', mention.SubjectNoteDisplay, mention.NoteTitle, mention.NoteText, mention.NoteExcerpt)));
        if (string.IsNullOrWhiteSpace(noteNumber)) return null;

        return OfficialSubjectIndexCandidates(subjectIndex, file)
            .FirstOrDefault(x => x.NoteNumber.Equals(noteNumber, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasOfficialSubjectIndexForBulletin(IReadOnlyList<FurrielSubjectIndexEntry>? entries, FurrielBulletinFile file)
    {
        if (entries is null || entries.Count == 0) return false;
        var bulletin = CanonicalFurrielBulletin(file.Bulletin, file.Date, file.OriginalName, file.StoredPath);
        var bulletinNumber = BulletinNumber(bulletin);
        if (bulletinNumber is null) return false;
        var date = ParseDate(file.Date);
        var year = BulletinYear(bulletin);
        return entries.Any(x =>
            BulletinNumber(x.BulletinNumber) == bulletinNumber
            && (date is null || ParseDate(x.BulletinDate) is not { } entryDate || entryDate.Date == date.Value.Date)
            && (string.IsNullOrWhiteSpace(year) || string.IsNullOrWhiteSpace(BulletinYear(x.BulletinNumber)) || BulletinYear(x.BulletinNumber) == year));
    }

    private static List<FurrielSubjectIndexEntry> OfficialSubjectIndexCandidates(IReadOnlyList<FurrielSubjectIndexEntry>? entries, FurrielBulletinFile file)
    {
        if (entries is null || entries.Count == 0) return [];
        var bulletin = CanonicalFurrielBulletin(file.Bulletin, file.Date, file.OriginalName, file.StoredPath);
        var bulletinNumber = BulletinNumber(bulletin);
        if (bulletinNumber is null) return [];
        var date = ParseDate(file.Date);
        var year = BulletinYear(bulletin);

        return entries
            .Where(IsTrustworthySubjectIndexEntry)
            .Where(x =>
                BulletinNumber(x.BulletinNumber) == bulletinNumber
                || (date is not null && IsFurrielBulletinPdf(file) && ParseDate(x.BulletinDate) is { } entryDateByDate && entryDateByDate.Date == date.Value.Date))
            .Where(x => date is null || ParseDate(x.BulletinDate) is not { } entryDate || entryDate.Date == date.Value.Date)
            .Where(x => string.IsNullOrWhiteSpace(year) || string.IsNullOrWhiteSpace(BulletinYear(x.BulletinNumber)) || BulletinYear(x.BulletinNumber) == year)
            .ToList();
    }

    private static bool IsTrustworthySubjectIndexEntry(FurrielSubjectIndexEntry entry)
        => IsTrustworthyFurrielTitle(entry.Subject, entry.NoteType);

    private static FurrielSubjectIndexEntry? FindOfficialSubjectIndexByLetterAnchor(
        FurrielBulletinFile file,
        BulletinMentionItem mention,
        IReadOnlyList<FurrielIndexedLine> lines,
        IReadOnlyList<FurrielSubjectIndexEntry>? subjectIndex)
    {
        if (lines.Count == 0) return null;
        var candidates = OfficialSubjectIndexCandidates(subjectIndex, file);
        if (candidates.Count == 0) return null;

        var personLineIndex = FindStrongMilitaryLineIndex(lines, mention);
        if (personLineIndex < 0) return null;

        var anchorIndex = FindLetterNoteAnchorIndex(lines, personLineIndex);
        if (anchorIndex < 0) return null;

        var title = ResolveFurrielTitleAtLetterAnchor(lines, anchorIndex, personLineIndex);
        if (string.IsNullOrWhiteSpace(title.Subject)) return null;

        return ValidateLetterAnchorAgainstOfficialIndex(candidates, title, lines[anchorIndex].Page, lines[personLineIndex].Page);
    }

    private static int FindStrongMilitaryLineIndex(IReadOnlyList<FurrielIndexedLine> lines, BulletinMentionItem mention)
    {
        var nameNorm = Normalize(mention.MentionedMilitaryName);
        var nameTokens = nameNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 2).ToList();
        if (nameTokens.Count < 2) nameNorm = string.Empty;

        var docs = new[] { mention.MentionedMilitaryCpf, mention.MentionedMilitaryPrecCp }
            .Select(Digits)
            .Where(x => x.Length >= 5)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(nameNorm) && docs.Count == 0) return -1;

        var expectedPage = mention.PageNumber ?? 0;
        var bestIndex = -1;
        var bestScore = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (expectedPage > 0 && Math.Abs(lines[i].Page - expectedPage) > 1) continue;
            var start = Math.Max(0, i - 1);
            var end = Math.Min(lines.Count - 1, i + 1);
            var windowText = string.Join(' ', lines.Skip(start).Take(end - start + 1).Select(x => x.Text));
            var windowNorm = Normalize(windowText);
            var windowDigits = Digits(windowText);

            var score = 0;
            if (!string.IsNullOrWhiteSpace(nameNorm) && windowNorm.Contains(nameNorm, StringComparison.Ordinal)) score += 260;
            if (nameTokens.Count >= 3 && nameTokens.All(token => Regex.IsMatch(windowNorm, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase))) score += 160;
            foreach (var doc in docs)
                if (windowDigits.Contains(doc, StringComparison.Ordinal)) score += 360;
            if (score == 0) continue;
            if (expectedPage > 0 && lines[i].Page == expectedPage) score += 80;
            if (IsNameLine(lines[i].Text)) score += 35;

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        return bestScore >= 240 ? bestIndex : -1;
    }

    private static int FindLetterNoteAnchorIndex(IReadOnlyList<FurrielIndexedLine> lines, int personLineIndex)
    {
        personLineIndex = Math.Clamp(personLineIndex, 0, lines.Count - 1);
        var first = Math.Max(0, personLineIndex - 260);
        for (var i = personLineIndex; i >= first; i--)
        {
            var text = CleanSpaces(lines[i].Text);
            if (IsFurrielLetterAnchor(text)) return i;
            if (i < personLineIndex && IsHardNoteBoundary(text) && personLineIndex - i > 18) break;
        }
        return -1;
    }

    private static (string Subject, string Note) ResolveFurrielTitleAtLetterAnchor(IReadOnlyList<FurrielIndexedLine> lines, int anchorIndex, int personLineIndex)
    {
        var merged = CleanSpaces(lines[anchorIndex].Text);
        var parsed = TryParseFurrielTitle(merged);
        if (!string.IsNullOrWhiteSpace(parsed.Subject)) return parsed;

        for (var j = anchorIndex + 1; j <= Math.Min(personLineIndex, anchorIndex + 5) && j < lines.Count; j++)
        {
            var next = CleanSpaces(lines[j].Text);
            if (next.Length == 0 || IsStructuralLine(next) || IsFurrielLetterAnchor(next) || IsNameLine(next) || IsHardNoteBoundary(next)) break;
            if (Regex.IsMatch(next, @"^(?:Seja|No requerimento|Em virtude|Tendo em vista|Conforme|Referente|O militar|A militar|Os militares|As militares)\b", RegexOptions.IgnoreCase)) break;
            if (Regex.IsMatch(next, @"\b(?:CPF|Prec[- ]?CP|IDT|R\$|Valor|Banco|Ag[êe]ncia|Conta)\b", RegexOptions.IgnoreCase)) break;
            merged = CleanSpaces(merged + " " + next);
            parsed = TryParseFurrielTitle(merged);
            if (!string.IsNullOrWhiteSpace(parsed.Subject)) return parsed;
        }
        return (string.Empty, string.Empty);
    }

    private static FurrielSubjectIndexEntry? ValidateLetterAnchorAgainstOfficialIndex(
        IReadOnlyList<FurrielSubjectIndexEntry> candidates,
        (string Subject, string Note) title,
        int anchorPage,
        int personPage)
    {
        var subject = CleanDisplaySubject(title.Subject);
        var note = CleanDisplayNote(title.Note, subject);
        if (string.IsNullOrWhiteSpace(subject) || IsForbiddenFurrielFreeTextTitle(ComposeSubjectNote(subject, note, string.Empty))) return null;

        var subjectNorm = Normalize(subject);
        var noteNorm = Normalize(note);
        var displayNorm = Normalize(ComposeSubjectNote(subject, note, string.Empty));
        var scored = candidates.Select(entry =>
        {
            var entrySubject = CleanDisplaySubject(entry.Subject);
            var entryNote = CleanDisplayNote(entry.NoteType, entrySubject);
            var entryDisplay = ComposeSubjectNote(entrySubject, entryNote, entry.SubjectNoteDisplay);
            var entrySubjectNorm = Normalize(entrySubject);
            var entryNoteNorm = Normalize(entryNote);
            var entryDisplayNorm = Normalize(entryDisplay);

            var titleScore = 0;
            if (displayNorm.Length > 0 && entryDisplayNorm == displayNorm) titleScore += 1200;
            if (displayNorm.Length > 0 && (entryDisplayNorm.Contains(displayNorm, StringComparison.Ordinal) || displayNorm.Contains(entryDisplayNorm, StringComparison.Ordinal))) titleScore += 820;
            if (subjectNorm.Length > 0 && entrySubjectNorm == subjectNorm) titleScore += 620;
            if (noteNorm.Length > 0 && entryNoteNorm == noteNorm) titleScore += 430;
            if (noteNorm.Length > 0 && entryNoteNorm.Length > 0 && (entryNoteNorm.Contains(noteNorm, StringComparison.Ordinal) || noteNorm.Contains(entryNoteNorm, StringComparison.Ordinal))) titleScore += 260;
            titleScore += SubjectIndexTokenScore(entrySubjectNorm, subjectNorm, 40);
            titleScore += SubjectIndexTokenScore(entryNoteNorm, noteNorm, 34);

            var pageScore = 0;
            if (entry.Page > 0)
            {
                if (anchorPage > 0 && entry.Page == anchorPage) pageScore += 240;
                else if (personPage > 0 && entry.Page == personPage) pageScore += 180;
                else if (anchorPage > 0 && Math.Abs(entry.Page - anchorPage) <= 1) pageScore += 90;
                else if (personPage > 0 && Math.Abs(entry.Page - personPage) <= 1) pageScore += 70;
            }

            return (Entry: entry, TitleScore: titleScore, Score: titleScore + pageScore);
        })
        .OrderByDescending(x => x.Score)
        .ThenBy(x => Math.Abs((x.Entry.Page <= 0 ? anchorPage : x.Entry.Page) - anchorPage))
        .ThenBy(x => x.Entry.NoteNumber, StringComparer.OrdinalIgnoreCase)
        .ToList();

        if (scored.Count == 0) return null;
        var best = scored[0];
        var second = scored.Count > 1 ? scored[1] : default;

        if (best.TitleScore >= 1050) return best.Entry;
        if (best.TitleScore >= 760 && (second.Entry is null || best.Score - second.Score >= 80)) return best.Entry;
        if (best.TitleScore >= 620 && !string.IsNullOrWhiteSpace(noteNorm) && (second.Entry is null || best.Score - second.Score >= 140)) return best.Entry;
        return null;
    }

    private static bool ShouldSearchOfficialSubjectSegments(string query, FurrielMilitaryOption? military, string consequenceFilter)
        => military is not null || !string.IsNullOrWhiteSpace(query) || !consequenceFilter.Equals("Todos", StringComparison.OrdinalIgnoreCase);

    private static string FurrielSearchResultLogicalKey(FurrielSearchResult row, FurrielMilitaryOption? military)
    {
        var personKey = "militar:" + Normalize(FirstNonEmpty(row.FullName, row.Military, row.PdfSearchTerm));
        if (military is not null)
        {
            if (military.Id > 0) personKey = $"id:{military.Id}";
            else if (!string.IsNullOrWhiteSpace(military.FullName)) personKey = "nome:" + Normalize(military.FullName);
            else
            {
                var docs = Digits(string.Join(' ', military.Cpf, military.PrecCp, military.Identity));
                if (docs.Length >= 5) personKey = "doc:" + docs;
            }
        }

        var parsed = ParseSubjectIndexTitle(row.SubjectNoteDisplay);
        var subject = CleanDisplaySubject(FirstNonEmpty(row.Subject, parsed.Subject));
        var note = CleanDisplayNote(parsed.Note, subject);
        return string.Join('|',
            personKey,
            Normalize(row.Bulletin),
            Normalize(row.Bar),
            row.Page.ToString(PtBr),
            Normalize(subject),
            Normalize(note));
    }

    private static List<FurrielIndexedLine> ResolveOfficialSubjectSegment(
        IReadOnlyList<FurrielIndexedLine> lines,
        IReadOnlyList<FurrielSubjectIndexEntry> candidates,
        FurrielSubjectIndexEntry entry)
    {
        var anchorIndex = FindOfficialSubjectEntryAnchor(lines, candidates, entry);
        if (anchorIndex < 0) return [];

        var endIndex = lines.Count;
        for (var i = anchorIndex + 1; i < lines.Count; i++)
        {
            var text = CleanSpaces(lines[i].Text);
            if (IsFurrielLetterAnchor(text))
            {
                endIndex = i;
                break;
            }

            if (i - anchorIndex > 4 && IsHardNoteBoundary(text))
            {
                endIndex = i;
                break;
            }

            if (entry.Page > 0 && lines[i].Page > entry.Page + 3 && i - anchorIndex > 20)
            {
                endIndex = i;
                break;
            }

            if (i - anchorIndex >= 650)
            {
                endIndex = i;
                break;
            }
        }

        return lines.Skip(anchorIndex).Take(Math.Max(1, endIndex - anchorIndex)).ToList();
    }

    private static int FindOfficialSubjectEntryAnchor(
        IReadOnlyList<FurrielIndexedLine> lines,
        IReadOnlyList<FurrielSubjectIndexEntry> candidates,
        FurrielSubjectIndexEntry entry)
    {
        var bestIndex = -1;
        var bestScore = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var text = CleanSpaces(lines[i].Text);
            if (!IsFurrielLetterAnchor(text)) continue;
            if (entry.Page > 0 && Math.Abs(lines[i].Page - entry.Page) > 2) continue;

            var title = ResolveFurrielTitleAtLetterAnchor(lines, i, Math.Min(lines.Count - 1, i + 8));
            if (string.IsNullOrWhiteSpace(title.Subject)) continue;

            var official = ValidateLetterAnchorAgainstOfficialIndex(candidates, title, lines[i].Page, lines[i].Page);
            if (official is null || !SameOfficialSubjectIndexEntry(official, entry)) continue;

            var score = OfficialEntryTitleScore(entry, title);
            if (entry.Page > 0)
            {
                if (lines[i].Page == entry.Page) score += 240;
                else if (Math.Abs(lines[i].Page - entry.Page) == 1) score += 80;
                else score -= 120;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestScore >= 620 ? bestIndex : -1;
    }

    private static bool SameOfficialSubjectIndexEntry(FurrielSubjectIndexEntry left, FurrielSubjectIndexEntry right)
    {
        if (!string.IsNullOrWhiteSpace(left.Id) && left.Id.Equals(right.Id, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(left.NoteNumber) && left.NoteNumber.Equals(right.NoteNumber, StringComparison.OrdinalIgnoreCase)
            && BulletinNumber(left.BulletinNumber) == BulletinNumber(right.BulletinNumber))
            return true;

        var leftSubject = CleanDisplaySubject(left.Subject);
        var leftNote = CleanDisplayNote(left.NoteType, leftSubject);
        var rightSubject = CleanDisplaySubject(right.Subject);
        var rightNote = CleanDisplayNote(right.NoteType, rightSubject);
        return left.Page == right.Page
               && BulletinNumber(left.BulletinNumber) == BulletinNumber(right.BulletinNumber)
               && Normalize(ComposeSubjectNote(leftSubject, leftNote, left.SubjectNoteDisplay)) == Normalize(ComposeSubjectNote(rightSubject, rightNote, right.SubjectNoteDisplay));
    }

    private static int OfficialEntryTitleScore(FurrielSubjectIndexEntry entry, (string Subject, string Note) title)
    {
        var subject = CleanDisplaySubject(title.Subject);
        var note = CleanDisplayNote(title.Note, subject);
        var displayNorm = Normalize(ComposeSubjectNote(subject, note, string.Empty));
        var subjectNorm = Normalize(subject);
        var noteNorm = Normalize(note);

        var entrySubject = CleanDisplaySubject(entry.Subject);
        var entryNote = CleanDisplayNote(entry.NoteType, entrySubject);
        var entryDisplayNorm = Normalize(ComposeSubjectNote(entrySubject, entryNote, entry.SubjectNoteDisplay));
        var entrySubjectNorm = Normalize(entrySubject);
        var entryNoteNorm = Normalize(entryNote);

        var score = 0;
        if (displayNorm.Length > 0 && entryDisplayNorm == displayNorm) score += 1200;
        if (displayNorm.Length > 0 && (entryDisplayNorm.Contains(displayNorm, StringComparison.Ordinal) || displayNorm.Contains(entryDisplayNorm, StringComparison.Ordinal))) score += 820;
        if (subjectNorm.Length > 0 && entrySubjectNorm == subjectNorm) score += 620;
        if (noteNorm.Length > 0 && entryNoteNorm == noteNorm) score += 430;
        if (noteNorm.Length > 0 && entryNoteNorm.Length > 0 && (entryNoteNorm.Contains(noteNorm, StringComparison.Ordinal) || noteNorm.Contains(entryNoteNorm, StringComparison.Ordinal))) score += 260;
        score += SubjectIndexTokenScore(entrySubjectNorm, subjectNorm, 40);
        score += SubjectIndexTokenScore(entryNoteNorm, noteNorm, 34);
        return score;
    }

    private static bool OfficialSegmentMatchesSearch(string segmentText, FurrielMilitaryOption? military, SearchProfile profile)
    {
        var normalized = Normalize(segmentText);
        var digits = Digits(segmentText);
        if (military is null) return MatchesProfile(normalized, digits, profile);

        var selectedName = Normalize(military.FullName);
        if (selectedName.Length >= 6 && normalized.Contains(selectedName, StringComparison.Ordinal)) return true;

        var selectedDigits = new[] { military.Cpf, military.PrecCp, military.Identity }
            .Select(Digits)
            .Where(x => x.Length >= 5)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selectedDigits.Any(x => digits.Contains(x, StringComparison.Ordinal))) return true;

        var nameTokens = Tokens(military.FullName, 2);
        if (nameTokens.Count >= 3)
        {
            var haystack = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (nameTokens.All(haystack.Contains)) return true;
            var hits = nameTokens.Count(haystack.Contains);
            if (hits >= Math.Min(4, nameTokens.Count) && haystack.Contains(nameTokens[^1])) return true;
        }

        return MatchesProfile(normalized, digits, profile);
    }

    private static (bool HasConsequence, bool IsFurrielConsequence, string Text) ExtractOfficialSegmentConsequence(string segmentText)
    {
        var match = Regex.Match(segmentText, @"\b(?:Em\s+)?consequ[êe]ncia\b.*", RegexOptions.IgnoreCase);
        if (!match.Success) return (false, false, string.Empty);

        var text = Truncate(match.Value, 650);
        var normalized = Normalize(text);
        var isFurriel = normalized.Contains("furriel", StringComparison.Ordinal)
            || normalized.Contains("secretaria", StringComparison.Ordinal)
            || normalized.Contains("pagamento", StringComparison.Ordinal)
            || normalized.Contains("sippes", StringComparison.Ordinal)
            || normalized.Contains("cpex", StringComparison.Ordinal)
            || normalized.Contains("ordenador de despesas", StringComparison.Ordinal);
        return (true, isFurriel, text);
    }

    private static string ExtractOfficialSegmentMilitaryDisplay(
        IReadOnlyList<FurrielIndexedLine> segment,
        string query,
        FurrielMilitaryOption? military,
        SearchProfile profile)
    {
        if (military is not null) return military.DisplayLabel;
        if (segment.Count == 0) return PlainPdfSearchText(query);

        var matchIndex = BestOfficialSegmentMatchIndex(segment, profile);
        if (matchIndex >= 0)
        {
            if (!string.IsNullOrWhiteSpace(query) && IsLikelyMatchedPersonLine(segment[matchIndex], profile))
                return Truncate(CleanSpaces(segment[matchIndex].Text), 120);

            if (IsNameLine(segment[matchIndex].Text)) return CleanSpaces(segment[matchIndex].Text);
            for (var offset = 1; offset <= 5; offset++)
            {
                var before = matchIndex - offset;
                if (before >= 0 && IsNameLine(segment[before].Text)) return CleanSpaces(segment[before].Text);
                var after = matchIndex + offset;
                if (after < segment.Count && IsNameLine(segment[after].Text)) return CleanSpaces(segment[after].Text);
            }

            var matched = CleanSpaces(segment[matchIndex].Text);
            if (!string.IsNullOrWhiteSpace(matched)) return Truncate(matched, 120);
        }

        if (!string.IsNullOrWhiteSpace(query)) return PlainPdfSearchText(query);

        var probable = ExtractProbableName(segment, string.Empty, profile);
        return !string.IsNullOrWhiteSpace(probable) ? CleanSpaces(probable) : PlainPdfSearchText(query);
    }

    private static bool IsLikelyMatchedPersonLine(FurrielIndexedLine row, SearchProfile profile)
    {
        var text = CleanSpaces(row.Text);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 180) return false;
        if (!MatchesProfile(row.Normalized, row.Digits, profile)) return false;
        if (IsNameLine(text)) return true;
        if (profile.DigitValues.Any(x => row.Digits.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
        if (profile.Names.Any(x => x.Length >= 3 && row.Normalized.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
        return profile.TokenGroups.Any(group => group.Count >= 2 && group.All(x => row.Normalized.Contains(x, StringComparison.OrdinalIgnoreCase)));
    }

    private static int BestOfficialSegmentMatchIndex(IReadOnlyList<FurrielIndexedLine> segment, SearchProfile profile)
    {
        var bestIndex = -1;
        var bestScore = 0;
        for (var i = 0; i < segment.Count; i++)
        {
            var row = segment[i];
            if (!MatchesProfile(row.Normalized, row.Digits, profile)) continue;

            var score = 160;
            if (IsNameLine(row.Text)) score += 110;
            if (profile.DigitValues.Any(x => row.Digits.Contains(x, StringComparison.OrdinalIgnoreCase))) score += 120;
            if (profile.Names.Any(x => x.Length >= 3 && row.Normalized.Contains(x, StringComparison.OrdinalIgnoreCase))) score += 90;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private static bool IsFurrielLetterAnchor(string text)
    {
        var match = Regex.Match(CleanSpaces(text), @"^(?<mark>[a-z]{1,2})\.\s+(?<rest>\S)", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        var rest = match.Groups["rest"].Value;
        return rest.Length > 0 && !char.IsDigit(rest[0]);
    }

    private static int SubjectIndexTokenScore(string title, string context, int weight)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(context)) return 0;
        var tokens = title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 4 && !StopWords.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tokens.Count == 0) return 0;
        var hits = tokens.Count(token => Regex.IsMatch(context, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase));
        if (hits == 0) return 0;
        return hits * weight + (hits == tokens.Count ? 80 : 0);
    }

    private static string ExtractSisbolNoteNumber(string value)
    {
        var text = value ?? string.Empty;
        var match = Regex.Match(text, @"\bNota\s*(?:n[ºo°]?|nr\.?)?\s*[:#-]?\s*(\d{2,8})\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static bool IsExactSelectedMilitaryMention(BulletinMentionItem mention, FurrielMilitaryOption military, string normalizedRawEvidence, string rawDigits)
    {
        var selectedName = Normalize(military.FullName);
        var mentionName = Normalize(mention.MentionedMilitaryName);
        var selectedDigits = new[] { military.Cpf, military.PrecCp, military.Identity }
            .Select(Digits)
            .Where(x => x.Length >= 5)
            .ToList();
        var mentionDigits = Digits(string.Join(' ', mention.MentionedMilitaryCpf, mention.MentionedMilitaryPrecCp));

        var evidenceHasSelectedName = selectedName.Length >= 6 && normalizedRawEvidence.Contains(selectedName, StringComparison.Ordinal);
        var evidenceHasSelectedDoc = selectedDigits.Count > 0 && selectedDigits.Any(x => rawDigits.Contains(x, StringComparison.Ordinal) || mentionDigits.Contains(x, StringComparison.Ordinal));
        var selectedNameTokens = Tokens(military.FullName, 2);
        var evidenceHasStrongNameTokens = false;
        if (selectedNameTokens.Count >= 3)
        {
            var haystack = normalizedRawEvidence.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hits = selectedNameTokens.Count(haystack.Contains);
            evidenceHasStrongNameTokens = hits >= Math.Min(4, selectedNameTokens.Count) && haystack.Contains(selectedNameTokens[^1]);
        }

        // Mesmo quando o parser antigo salvou MilitaryId, só confiar se o PDF comprovar
        // com nome completo ou documento. Isso elimina falsa vinculação por nome de guerra
        // comum, como LUCCA HABAEB x LUCCA SOARES MACHADO.
        if (military.Id > 0 && mention.MilitaryId.HasValue && mention.MilitaryId.Value == military.Id)
            return evidenceHasSelectedName || evidenceHasSelectedDoc || evidenceHasStrongNameTokens;

        if (selectedName.Length > 0 && mentionName.Length > 0)
            return mentionName.Equals(selectedName, StringComparison.Ordinal) && (evidenceHasSelectedName || evidenceHasSelectedDoc || evidenceHasStrongNameTokens);

        if (selectedDigits.Count > 0 && mentionDigits.Length > 0 && selectedDigits.Any(x => mentionDigits.Contains(x, StringComparison.Ordinal)))
            return true;

        // Último recurso: menção sem militar identificado, mas o texto bruto da nota contém
        // o nome completo/documento selecionado. Nunca usar somente nome de guerra aqui.
        if (mention.MilitaryId.HasValue || mentionName.Length > 0 || mentionDigits.Length > 0) return false;
        return evidenceHasSelectedName || evidenceHasSelectedDoc || evidenceHasStrongNameTokens;
    }

    private static string FurrielMentionPersonKey(BulletinMentionItem mention, FurrielMilitaryOption? military)
    {
        if (military is not null)
        {
            if (military.Id > 0) return $"id:{military.Id}";
            var selectedName = Normalize(military.FullName);
            if (selectedName.Length > 0) return $"nome:{selectedName}";
            var selectedDigits = Digits(string.Join(' ', military.Cpf, military.PrecCp, military.Identity));
            if (selectedDigits.Length >= 5) return $"doc:{selectedDigits}";
        }

        if (mention.MilitaryId.HasValue && mention.MilitaryId.Value > 0) return $"id:{mention.MilitaryId.Value}";
        var mentionName = Normalize(mention.MentionedMilitaryName);
        if (mentionName.Length > 0) return $"nome:{mentionName}";
        var mentionDigits = Digits(string.Join(' ', mention.MentionedMilitaryCpf, mention.MentionedMilitaryPrecCp));
        if (mentionDigits.Length >= 5) return $"doc:{mentionDigits}";
        return $"militar:{Normalize(mention.DisplayMilitary)}";
    }

    public async Task ExportCsvAsync(IEnumerable<FurrielSearchResult> results, string path, CancellationToken cancellationToken = default)
    {
        static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
        var sb = new StringBuilder();
        sb.AppendLine("Militar;Tipo;Boletim;BAR;Data;Pagina;Assinado;Assunto_Nota;Consequencia;Texto_Nota;Arquivo;PDF_Assinado");
        foreach (var row in results)
        {
            sb.AppendLine(string.Join(';',
                Csv(row.Military), Csv(row.Type), Csv(row.Bulletin), Csv(row.Bar), Csv(row.Date), row.Page.ToString(PtBr),
                Csv(row.Signed), Csv(row.SubjectNoteDisplay), Csv(row.ConsequenceDisplay), Csv(row.NoteText), Csv(row.PdfPath), Csv(row.SignedPdfPath)));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    public async Task ExportSubjectIndexCsvAsync(FurrielIndexStore store, string path, CancellationToken cancellationToken = default)
    {
        static string Csv(string? value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
        var rows = (store.SubjectIndex ?? [])
            .OrderBy(x => ParseDate(x.BulletinDate) ?? DateTime.MaxValue)
            .ThenBy(x => BulletinNumber(x.BulletinNumber) ?? int.MaxValue)
            .ThenBy(x => x.Page)
            .ThenBy(x => x.NoteNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sb = new StringBuilder();
        sb.AppendLine("Data;Aditamento;Pagina;Nota_SisBol;Usuario;Assunto;Nota_Tipo;Assunto_Nota;PDF_Indice");
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(';',
                Csv(row.BulletinDate),
                Csv(row.BulletinNumber),
                row.Page.ToString(PtBr),
                Csv(row.NoteNumber),
                Csv(row.SisbolUser),
                Csv(row.Subject),
                Csv(row.NoteType),
                Csv(row.SubjectNoteDisplay),
                Csv(row.SourcePdfPath)));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    public async Task RemoveFileAsync(FurrielIndexStore store, FurrielBulletinFile file, bool deleteStoredFile = true, CancellationToken cancellationToken = default)
    {
        store.Files.RemoveAll(x => x.Id.Equals(file.Id, StringComparison.OrdinalIgnoreCase));
        if (deleteStoredFile && File.Exists(file.StoredPath))
        {
            try { File.Delete(file.StoredPath); } catch { }
        }
        await SaveIndexAsync(store, cancellationToken);
    }

    public async Task RemoveFilesAsync(
        FurrielIndexStore store,
        IEnumerable<FurrielBulletinFile> files,
        bool deleteStoredFiles = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var selected = files
            .Where(x => x is not null)
            .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0) return;

        var ids = selected.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var signedPaths = selected
            .Select(x => GetSignedInfo(store, x)?.Path)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = selected[i];
            progress?.Report($"Removendo ADT {i + 1}/{selected.Count}: {file.OriginalName}");
            if (deleteStoredFiles && File.Exists(file.StoredPath))
            {
                try { File.Delete(file.StoredPath); } catch { }
            }
        }

        foreach (var signedPath in signedPaths)
        {
            if (!deleteStoredFiles || !File.Exists(signedPath)) continue;
            try { File.Delete(signedPath); } catch { }
        }

        store.Files = store.Files.Where(x => !ids.Contains(x.Id)).ToList();
        store.SignedFiles = store.SignedFiles
            .Where(x => !signedPaths.Contains(x.Value.Path))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        await SaveIndexAsync(store, cancellationToken);
    }

    public async Task RemoveAllBulletinsAsync(
        FurrielIndexStore store,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Removendo PDFs comuns e assinados...");
        try { Directory.Delete(PdfDirectory, true); } catch { }
        try { Directory.Delete(SignedDirectory, true); } catch { }
        Directory.CreateDirectory(PdfDirectory);
        Directory.CreateDirectory(SignedDirectory);
        store.Files = [];
        store.SignedFiles = new Dictionary<string, FurrielSignedFileInfo>(StringComparer.OrdinalIgnoreCase);
        await SaveIndexAsync(store, cancellationToken);
    }

    public async Task ClearAsync(FurrielIndexStore store, CancellationToken cancellationToken = default)
    {
        try { Directory.Delete(PdfDirectory, true); } catch { }
        try { Directory.Delete(SignedDirectory, true); } catch { }
        Directory.CreateDirectory(PdfDirectory);
        Directory.CreateDirectory(SignedDirectory);

        // Não limpar as listas existentes enquanto a tela/DataGrid pode estar enumerando.
        // Trocar as referências elimina o erro "Collection was modified".
        store.Files = [];
        store.SignedFiles = new Dictionary<string, FurrielSignedFileInfo>(StringComparer.OrdinalIgnoreCase);
        store.SubjectIndex = [];
        await SaveIndexAsync(store, cancellationToken);
    }

    public FurrielSignedFileInfo? GetSignedInfo(FurrielIndexStore store, FurrielBulletinFile file)
    {
        foreach (var key in LookupKeys(file))
        {
            if (store.SignedFiles.TryGetValue(key, out var info) && File.Exists(info.Path)) return info;
        }

        var number = BulletinNumber(file.Bulletin);
        if (number is null) return null;
        foreach (var info in store.SignedFiles.Values.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(info.Path) || BulletinNumber(info.Bulletin) != number) continue;
            var fileYear = BulletinYear(file.Bulletin);
            var signedYear = BulletinYear(info.Bulletin);
            if (!string.IsNullOrWhiteSpace(fileYear) && !string.IsNullOrWhiteSpace(signedYear) && fileYear != signedYear) continue;
            if (!string.IsNullOrWhiteSpace(file.Bar) && !string.IsNullOrWhiteSpace(info.Bar) && Normalize(file.Bar) != Normalize(info.Bar)) continue;
            return info;
        }
        return null;
    }

    public string GetSignedPath(FurrielIndexStore store, FurrielBulletinFile file)
        => GetSignedInfo(store, file)?.Path ?? string.Empty;

    public async Task SaveAsBulletinKeysAsync(FurrielBulletinFile file, CancellationToken cancellationToken = default)
    {
        var number = BulletinNumber(file.Bulletin)?.ToString(PtBr) ?? CleanBulletinNumber(file.Bulletin);
        var date = ParseDate(file.Date);
        var dateBr = date?.ToString("dd/MM/yyyy", PtBr) ?? file.Date;
        var dateAbbreviation = date is null ? dateBr : $"{date:dd} {MonthAbbreviation(date.Value.Month)} {date:yy}";
        var reference = $"Adt Furr Nr {number}{(string.IsNullOrWhiteSpace(dateAbbreviation) ? string.Empty : $", de {dateAbbreviation}")}, {OrganizationIdentity.BulletinIssuer}";
        var path = Path.Combine(_paths.DataDirectory, "boletim_chaves_boletim.json");
        JsonObject obj;
        try
        {
            obj = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject ?? new JsonObject() : new JsonObject();
        }
        catch { obj = new JsonObject(); }
        obj["BI_REFERENCIA"] = reference;
        obj["REFERENCIA_BOLETIM"] = reference;
        obj["PUBLICACAO_BI"] = reference;
        obj["BOLETIM_REFERENCIA"] = reference;
        obj["BI_TIPO"] = "ADT FURRIEL";
        obj["BI_NUMERO"] = number;
        obj["NUM_BI"] = number;
        obj["DATA_BI"] = dateBr;
        obj["DATA_PUBLICACAO_BI"] = dateBr;
        obj["DATA_PUBLICACAO"] = dateBr;
        obj["DATA_PUBLICACAO_BI_ABREV"] = dateAbbreviation;
        obj["ADT_REFERENCIA"] = reference;
        obj["NUM_ADT"] = number;
        obj["DATA_ADT"] = dateBr;
        obj["BI_ORIGEM"] = "Aditamento Furriel";
        obj["_meta"] = new JsonObject
        {
            ["origem"] = "boletim_furriel_wpf",
            ["atualizado_em"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", PtBr)
        };
        await File.WriteAllTextAsync(path, obj.ToJsonString(JsonOptions), cancellationToken);
    }

    public void OpenPdf(string path, string searchText = "", int page = 0)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Arquivo PDF não encontrado.", path);
        ShellService.OpenPath(path);
        var term = PlainPdfSearchText(searchText);
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(term))
        {
            try { System.Windows.Clipboard.SetText(term); } catch { }
            _ = Task.Run(async () =>
            {
                await Task.Delay(1800);
                SendCtrlFAndPaste();
                await Task.Delay(650);
                PressKey(0x1B);
            });
        }
    }

    private async Task<(bool Created, FurrielBulletinFile Item)> RegisterCommonAsync(
        FurrielIndexStore store,
        string source,
        Dictionary<string, FurrielBulletinFile> byId,
        CancellationToken cancellationToken)
    {
        var indexed = await IndexPdfAsync(source, cancellationToken);
        if (!IsFurrielBulletinPdf(indexed))
            throw new InvalidDataException("O arquivo não parece ser Aditamento do Furriel. Use somente PDFs do ADT Furriel neste módulo.");
        indexed.SourcePath = Path.GetFullPath(source);
        indexed.SourceDirectory = Path.GetDirectoryName(indexed.SourcePath) ?? string.Empty;
        indexed.SourceOriginalName = Path.GetFileName(source);
        var destination = OrderedDestination(source, indexed, PdfDirectory, false);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) && !File.Exists(destination))
            File.Copy(source, destination, false);
        indexed.StoredPath = destination;
        foreach (var mention in indexed.Mentions) mention.SourceFilePath = destination;
        var created = !byId.TryGetValue(indexed.Id, out var old);
        if (old is not null)
        {
            var position = store.Files.IndexOf(old);
            if (position >= 0) store.Files[position] = indexed;
        }
        else store.Files.Add(indexed);
        return (created, indexed);
    }

    private async Task<bool> RegisterSignedAsync(FurrielIndexStore store, string source, FurrielBulletinFile? selectedHint, CancellationToken cancellationToken)
    {
        FurrielBulletinFile metadata;
        try { metadata = await IndexPdfAsync(source, cancellationToken); }
        catch
        {
            metadata = MetadataFromFilename(source);
            metadata.Id = HashFile(source);
            metadata.OriginalName = Path.GetFileName(source);
            metadata.StoredPath = Path.GetFullPath(source);
        }
        if (!IsFurrielBulletinPdf(metadata) && !IsLikelyFurrielFileName(source))
            throw new InvalidDataException("O arquivo assinado não parece ser Aditamento do Furriel. Selecione o PDF assinado correto do ADT Furriel.");
        metadata.SourcePath = Path.GetFullPath(source);
        metadata.SourceDirectory = Path.GetDirectoryName(metadata.SourcePath) ?? string.Empty;
        metadata.SourceOriginalName = Path.GetFileName(source);
        var resolved = ResolveSignedMetadata(store, metadata, selectedHint);
        var destination = OrderedDestination(source, resolved, SignedDirectory, true);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, destination, true);

        var info = new FurrielSignedFileInfo
        {
            Path = destination,
            OriginalName = Path.GetFileName(source),
            Bulletin = resolved.Bulletin,
            Bar = resolved.Bar,
            Date = resolved.Date,
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", PtBr),
            SourcePath = metadata.SourcePath,
            SourceDirectory = metadata.SourceDirectory,
            SourceOriginalName = metadata.SourceOriginalName
        };

        var existed = false;
        foreach (var key in LookupKeys(resolved))
        {
            if (store.SignedFiles.ContainsKey(key)) existed = true;
            store.SignedFiles[key] = info;
        }
        return !existed;
    }

    private bool NormalizeSignedFileNames(FurrielIndexStore store)
    {
        var changed = false;
        var groups = store.SignedFiles.Values
            .Where(info => !string.IsNullOrWhiteSpace(info.Path) && File.Exists(info.Path))
            .GroupBy(info => Path.GetFullPath(info.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            var currentPath = group.Key;
            var info = group.First();
            var number = BulletinNumber(info.Bulletin);
            if (number is null) continue;
            var year = BulletinYear(info.Bulletin);
            var resolved = store.Files
                .Where(file => BulletinNumber(file.Bulletin) == number)
                .Where(file => string.IsNullOrWhiteSpace(year) || string.IsNullOrWhiteSpace(BulletinYear(file.Bulletin)) || BulletinYear(file.Bulletin) == year)
                .Where(file => string.IsNullOrWhiteSpace(info.Bar) || string.IsNullOrWhiteSpace(file.Bar) || Normalize(file.Bar) == Normalize(info.Bar))
                .OrderByDescending(file => ParseDate(info.Date) is { } signedDate && ParseDate(file.Date) is { } commonDate && signedDate.Date == commonDate.Date)
                .ThenByDescending(file => Normalize(file.Bar) == Normalize(info.Bar))
                .FirstOrDefault();
            if (resolved is null || ParseDate(resolved.Date) is null) continue;

            var destination = OrderedDestination(currentPath, resolved, SignedDirectory, true);
            if (!Path.GetFullPath(destination).Equals(currentPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!File.Exists(destination))
                        File.Move(currentPath, destination);
                }
                catch
                {
                    continue;
                }
                currentPath = destination;
                changed = true;
            }

            foreach (var linked in group)
            {
                linked.Path = currentPath;
                linked.Bulletin = resolved.Bulletin;
                linked.Bar = resolved.Bar;
                linked.Date = resolved.Date;
            }
        }
        return changed;
    }

    private async Task<FurrielBulletinFile> IndexPdfAsync(string path, CancellationToken cancellationToken)
    {
        var pages = await ExtractPdfPagesAsync(path, cancellationToken);
        var metadata = ExtractMetadata(pages, path);
        var lines = new List<FurrielIndexedLine>();
        var major = string.Empty;
        var subject = string.Empty;
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            foreach (var raw in pages[pageIndex].Replace("\r\n", "\n").Split('\n'))
            {
                var line = CleanPdfLine(raw);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (IsHeadingLine(line))
                {
                    var label = FormatNoteTitle(HeadingLabel(line));
                    if (Regex.IsMatch(line, @"^\d+ª\s+Parte\b", RegexOptions.IgnoreCase) || Regex.IsMatch(line, @"^\d+\.\s+"))
                    {
                        major = label;
                        subject = label;
                    }
                    else subject = label;
                }
                lines.Add(new FurrielIndexedLine
                {
                    Page = pageIndex + 1,
                    Text = line,
                    Normalized = Normalize(line),
                    Digits = Digits(line),
                    Subject = string.IsNullOrWhiteSpace(subject) ? (string.IsNullOrWhiteSpace(major) ? "—" : major) : subject,
                    Major = string.IsNullOrWhiteSpace(major) ? "—" : major
                });
            }
        }
        metadata.Id = HashFile(path);
        metadata.OriginalName = Path.GetFileName(path);
        metadata.StoredPath = Path.GetFullPath(path);
        metadata.Pages = pages.Count;
        metadata.LineCount = lines.Count;
        metadata.IndexedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", PtBr);
        metadata.Lines = lines;
        var people = await LoadMilitaryOptionsAsync(SourceAll, cancellationToken);
        var identities = people.Select(x => new BulletinMilitaryIdentity
        {
            Id = x.Id, Rank = x.Rank, FullName = x.FullName, WarName = x.WarName,
            Cpf = x.Cpf, Identity = x.Identity, PrecCp = x.PrecCp
        }).ToList();
        metadata.Mentions = ProfessionalBulletinParser.Parse(
            pages, "ADT FURRIEL", metadata.Bulletin, ParseDate(metadata.Date), path, identities);
        metadata.ParserVersion = CurrentParserVersion;
        return metadata;
    }

    private async Task<List<string>> ExtractPdfPagesAsync(string path, CancellationToken cancellationToken)
    {
        // Extração nativa em C#: não depende de Python, pypdf, PyPDF2 ou Poppler.
        var pages = await App.PdfText.ExtractPagesAsync(path, cancellationToken);
        var result = pages.Select(NormalizeFurrielPageLayout).ToList();
        while (result.Count > 1 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
        if (result.Any(x => !string.IsNullOrWhiteSpace(x))) return result;
        throw new InvalidOperationException("O PDF não possui texto pesquisável. Use o arquivo original exportado pelo sistema ou aplique OCR antes de importar.");
    }

    private static string NormalizeFurrielPageLayout(string page)
    {
        var source = (page ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = source.Split('\n').Select(x => x ?? string.Empty).ToList();
        var output = new List<string>();
        for (var i = 0; i < rawLines.Count; i++)
        {
            var line = CleanSpaces(rawLines[i]);
            if (line.Length == 0)
            {
                output.Add(string.Empty);
                continue;
            }

            if (IsFurrielLetterAnchor(line))
            {
                while (i + 1 < rawLines.Count && IsFurrielHeadingContinuation(rawLines[i + 1], line))
                {
                    line = CleanSpaces(line + " " + rawLines[i + 1]);
                    i++;
                }
            }
            output.Add(line);
        }
        return string.Join('\n', output);
    }

    private static bool IsFurrielHeadingContinuation(string rawNext, string accumulated)
    {
        var text = CleanSpaces(rawNext);
        if (text.Length is < 3 or > 90) return false;
        if (IsStructuralLine(text) || IsNameLine(text) || IsHardNoteBoundary(text)) return false;
        if (IsFurrielLetterAnchor(text)) return false;
        if (Regex.IsMatch(text, @"^(?:Seja|No requerimento|Em virtude|Tendo em vista|Apresentou-se|Referente|Solicito|Faz jus|Deixa|Passa|Os militares|O militar|A militar)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(text, @"\b(?:CPF|Prec[- ]?CP|IDT|R\$|Valor|Banco|Ag[êe]ncia|Conta)\b", RegexOptions.IgnoreCase)) return false;
        var accumulatedClean = CleanSpaces(accumulated).Replace('–', '-').Replace('—', '-');
        var delimiter = Regex.Match(accumulatedClean, @"\s-\s*");
        if (delimiter.Success)
        {
            var noteSide = accumulatedClean[(delimiter.Index + delimiter.Length)..].Trim();
            var noteWords = Regex.Matches(noteSide, @"[A-Za-zÁÀÂÃÉÊÍÓÔÕÚÇáàâãéêíóôõúç0-9]+").Count;
            if (noteWords >= 2 || noteSide.Length >= 8) return false;
        }
        if (!Regex.IsMatch(CleanSpaces(accumulated + " " + text), @"\s[-–—]\s")) return false;
        return char.IsUpper(text.FirstOrDefault(char.IsLetter));
    }

    private static List<string> SplitPages(string output)
    {
        var pages = output.Split('\f').Select(x => x ?? string.Empty).ToList();
        while (pages.Count > 1 && string.IsNullOrWhiteSpace(pages[^1])) pages.RemoveAt(pages.Count - 1);
        return pages.Count == 0 ? [output] : pages;
    }

    private async Task<List<FurrielMilitaryOption>> LoadInactiveMilitaryAsync(CancellationToken cancellationToken)
    {
        var result = new List<FurrielMilitaryOption>();
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = _repository.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = true };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='lt_militares';";
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), PtBr) == 0) return result;
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, posto, nome, nome_guerra, cpf, prec_cp, idt, motivo, destino FROM lt_militares WHERE COALESCE(visivel,1)=1 ORDER BY id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string S(int i) => reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), PtBr) ?? string.Empty;
                var reason = S(7);
                var destination = S(8);
                result.Add(new FurrielMilitaryOption
                {
                    Id = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0), PtBr),
                    Rank = S(1), FullName = S(2), WarName = S(3), Cpf = S(4), PrecCp = S(5), Identity = S(6),
                    Situation = string.Join(" — ", new[] { reason, destination }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    Source = "Licenciado/Transferido"
                });
            }
        }
        catch (Exception ex) { await _log.WriteAsync("Falha ao carregar licenciados/transferidos no Aditamento Furriel.", ex); }
        return result;
    }

    private async Task<List<string>> ExpandSourcesAsync(IEnumerable<string> sources, List<string> errors, CancellationToken cancellationToken)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sources.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(raw);
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                             .Where(x => Path.GetExtension(x).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                                      || Path.GetExtension(x).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
                    await ExpandOneAsync(file);
            }
            else await ExpandOneAsync(path);
        }
        return found.OrderBy(x => IsSignedByName(x)).ThenBy(x => Path.GetFileName(x), StringComparer.CurrentCultureIgnoreCase).ToList();

        async Task ExpandOneAsync(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) { errors.Add($"Arquivo não encontrado: {path}"); return; }
            var extension = Path.GetExtension(path);
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(path)) found.Add(path);
                return;
            }
            if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return;
            var directory = Path.Combine(TempDirectory, "furriel_zip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using var archive = ZipFile.OpenRead(path);
                var number = 0;
                foreach (var entry in archive.Entries.Where(x => x.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var safe = SafeFileName(Path.GetFileName(entry.FullName));
                    var destination = Path.Combine(directory, $"{++number:0000}_{safe}");
                    await using var input = entry.Open();
                    await using var output = File.Create(destination);
                    await input.CopyToAsync(output, cancellationToken);
                    if (seen.Add(destination)) found.Add(destination);
                }
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
    }

    private static FurrielIndexStore NewIndex() => new() { SchemaVersion = SchemaVersion };

    private static List<FurrielSubjectIndexEntry> ParseFurrielSubjectIndex(IReadOnlyList<string> pages, string sourcePath, string sourceHash)
    {
        var result = new List<FurrielSubjectIndexEntry>();
        var currentSubject = string.Empty;
        var currentTitle = string.Empty;
        var continuation = new StringBuilder();

        foreach (var rawPage in pages)
        {
            var rows = (rawPage ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(CleanSpaces)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            foreach (var row in rows)
            {
                var line = row.Replace('–', '-').Replace('—', '-');

                if (line.StartsWith('»'))
                {
                    currentSubject = CleanSubjectIndexTitle(line.TrimStart('»'));
                    currentTitle = currentSubject;
                    continuation.Clear();
                    continuation.Append(currentTitle);
                    continue;
                }

                if (IsSubjectIndexStructuralLine(line)) continue;

                var adt = Regex.Match(line,
                    @"\b(?:Adt\s+Furr|BI)\s+N[ºo°]?\s*(?<num>\d{1,4})\s+de\s+(?<date>\d{2}/\d{2}/\d{4})\s+pag\.?\s*(?<page>\d+)\s*-\s*Nota:\s*(?<note>[0-9A-Za-z./-]+)\s*-\s*Usu[aá]rio:\s*(?<user>\S+)",
                    RegexOptions.IgnoreCase);
                if (adt.Success)
                {
                    if (continuation.Length > 0)
                    {
                        currentTitle = CleanSubjectIndexTitle(continuation.ToString());
                        continuation.Clear();
                    }

                    var parsed = ParseSubjectIndexTitle(currentTitle);
                    if (string.IsNullOrWhiteSpace(parsed.Subject)) continue;

                    var number = int.Parse(adt.Groups["num"].Value, PtBr).ToString(PtBr) + "/" + adt.Groups["date"].Value[^4..];
                    var pageNumber = int.TryParse(adt.Groups["page"].Value, NumberStyles.Integer, PtBr, out var page) ? page : 0;
                    var noteNumber = adt.Groups["note"].Value.Trim();
                    var (subject, noteType) = SplitKnownSubjectIndexNote(parsed.Subject, parsed.Note);
                    var display = ComposeSubjectNote(subject, noteType, currentTitle);
                    var idSeed = string.Join('|', sourceHash, number, adt.Groups["date"].Value, pageNumber.ToString(PtBr), noteNumber, Normalize(parsed.Subject), Normalize(parsed.Note));
                    result.Add(new FurrielSubjectIndexEntry
                    {
                        Id = ShortHash(idSeed),
                        SourcePdfPath = sourcePath,
                        SourcePdfHash = sourceHash,
                        BulletinNumber = number,
                        BulletinDate = adt.Groups["date"].Value,
                        Page = pageNumber,
                        NoteNumber = noteNumber,
                        SisbolUser = adt.Groups["user"].Value.Trim(),
                        Subject = subject,
                        NoteType = noteType,
                        SubjectNoteDisplay = display,
                        SearchTextNormalized = Normalize(string.Join(' ', number, adt.Groups["date"].Value, pageNumber, noteNumber, subject, noteType, display))
                    });
                    continue;
                }

                if (line.StartsWith('-'))
                {
                    var note = CleanSubjectIndexTitle(line);
                    currentTitle = string.IsNullOrWhiteSpace(currentSubject) || IsSubjectIndexContainerSubject(currentSubject)
                        ? note
                        : CleanSubjectIndexTitle(currentSubject + " - " + note);
                    continuation.Clear();
                    continuation.Append(currentTitle);
                    continue;
                }

                if (continuation.Length > 0 && !LooksLikeSubjectIndexGarbage(line))
                {
                    continuation.Append(' ').Append(line.Trim());
                    currentTitle = CleanSubjectIndexTitle(continuation.ToString());
                    if (!currentTitle.Contains(" - ", StringComparison.Ordinal))
                        currentSubject = currentTitle;
                }
            }
        }

        return result;
    }

    private static bool IsSubjectIndexStructuralLine(string line)
    {
        var clean = CleanSpaces(line);
        if (clean.Length == 0) return true;
        if (clean.StartsWith("MINISTÉRIO", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("MINISTERIO", StringComparison.OrdinalIgnoreCase)) return true;
        if (clean.StartsWith("EXÉRCITO", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("EXERCITO", StringComparison.OrdinalIgnoreCase)) return true;
        if (clean.StartsWith("CML -", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("4ª COMPANHIA", StringComparison.OrdinalIgnoreCase)) return true;
        if (clean.StartsWith("PEL POL", StringComparison.OrdinalIgnoreCase)) return true;
        if (clean.StartsWith("ÍNDICE REMISSIVO", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("INDICE REMISSIVO", StringComparison.OrdinalIgnoreCase)) return true;
        if (clean.StartsWith("(Continuação", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("(Continuacao", StringComparison.OrdinalIgnoreCase)) return true;
        if (Regex.IsMatch(clean, @"^-\s*Página\s+\d+\s*-$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(clean, @"^P[áa]gina\s+\d+\s*-\s*\(Continua[çc][ãa]o\b", RegexOptions.IgnoreCase)) return true;
        return Regex.IsMatch(clean, @"^[A-Z]$", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeSubjectIndexGarbage(string line)
    {
        var clean = CleanSpaces(line);
        if (clean.Length == 0) return true;
        if (clean.StartsWith('»')) return true;
        if (Regex.IsMatch(clean, @"\bAdt\s+Furr\s+N", RegexOptions.IgnoreCase)) return true;
        return IsSubjectIndexStructuralLine(clean);
    }

    private static bool IsSubjectIndexContainerSubject(string value)
    {
        var normalized = Normalize(value);
        return normalized is "pagamento pessoal" or "assuntos gerais e administrativos" or "outros assuntos" or "ocorrencia do militar no aditamento";
    }

    private static string StripSubjectIndexContainerPrefix(string value)
    {
        var clean = CleanDisplaySubject(value);
        if (string.IsNullOrWhiteSpace(clean)) return string.Empty;
        if (IsSubjectIndexContainerSubject(clean)) return string.Empty;

        var previous = string.Empty;
        while (!clean.Equals(previous, StringComparison.Ordinal))
        {
            previous = clean;
            clean = Regex.Replace(clean,
                @"^(?:\d+\.\s*)?(?:PAGAMENTO\s+PESSOAL|ASSUNTOS\s+GERAIS\s+E\s+ADMINISTRATIVOS|OUTROS\s+ASSUNTOS|OCORR[ÊE]NCIA\s+DO\s+MILITAR\s+NO\s+ADITAMENTO)(?:\s*[-–—]\s*|\s+)(?=\S)",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();
        }

        if (IsSubjectIndexContainerSubject(clean)) return string.Empty;
        return clean;
    }

    private static (string Subject, string Note) SplitKnownSubjectIndexNote(string subject, string note)
    {
        subject = StripSubjectIndexContainerPrefix(subject);
        note = CleanDisplayNote(note, subject);
        if (!string.IsNullOrWhiteSpace(note) || string.IsNullOrWhiteSpace(subject)) return (subject, note);

        var suffixes = new[]
        {
            "Despacho do Ordenador de Despesas",
            "Férias Radiológicas",
            "Saque de atrasados",
            "Saque de atrasado",
            "Ordem de Implantação",
            "Ordem de Saque",
            "Saque de Atrasado",
            "Implantação",
            "Solicitação",
            "Alteração",
            "Diferença",
            "Inclusão",
            "Exclusão",
            "Restituição",
            "Concessão",
            "Suspensão",
            "Anulação"
        };

        foreach (var suffix in suffixes.OrderByDescending(x => x.Length))
        {
            var match = Regex.Match(subject, $@"^(?<subject>.+?)\s+(?<note>{Regex.Escape(suffix)})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;

            var candidateSubject = StripSubjectIndexContainerPrefix(match.Groups["subject"].Value);
            var candidateNote = CleanDisplayNote(match.Groups["note"].Value, candidateSubject);
            if (!string.IsNullOrWhiteSpace(candidateSubject) && !string.IsNullOrWhiteSpace(candidateNote))
                return (candidateSubject, candidateNote);
        }

        return (subject, note);
    }

    private static void NormalizeSubjectIndexEntry(FurrielSubjectIndexEntry entry)
    {
        var (subject, note) = SplitKnownSubjectIndexNote(entry.Subject, entry.NoteType);
        if (string.IsNullOrWhiteSpace(note))
        {
            var parsed = ParseSubjectIndexTitle(entry.SubjectNoteDisplay);
            if (!IsSubjectIndexContainerSubject(parsed.Subject))
            {
                (subject, note) = SplitKnownSubjectIndexNote(parsed.Subject, parsed.Note);
            }
        }

        entry.Subject = subject;
        entry.NoteType = note;
        entry.SubjectNoteDisplay = ComposeSubjectNote(subject, note, entry.SubjectNoteDisplay);
        entry.SearchTextNormalized = Normalize(string.Join(' ', entry.BulletinNumber, entry.BulletinDate, entry.Page, entry.NoteNumber, entry.Subject, entry.NoteType, entry.SubjectNoteDisplay));
    }

    private static string CleanSubjectIndexTitle(string value)
    {
        var clean = CleanSpaces(value).Replace('–', '-').Replace('—', '-');
        clean = Regex.Replace(clean, @"^-\s*", string.Empty).Trim();
        return clean;
    }

    private static (string Subject, string Note) ParseSubjectIndexTitle(string value)
    {
        var clean = CleanSubjectIndexTitle(value);
        if (string.IsNullOrWhiteSpace(clean)) return (string.Empty, string.Empty);
        var parts = Regex.Split(clean, @"\s+-\s+").Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (parts.Length == 0) return (string.Empty, string.Empty);
        if (parts.Length == 1) return (CleanDisplaySubject(parts[0]), string.Empty);
        var subject = CleanDisplaySubject(string.Join(" - ", parts.Take(parts.Length - 1)));
        var note = CleanDisplayNote(parts[^1], subject);
        return (subject, note);
    }

    private static void NormalizeIndex(FurrielIndexStore store)
    {
        store.SchemaVersion = SchemaVersion;
        store.Files ??= [];
        store.SubjectIndex ??= [];
        foreach (var entry in store.SubjectIndex) NormalizeSubjectIndexEntry(entry);
        store.SubjectIndex = store.SubjectIndex
            .Where(IsTrustworthySubjectIndexEntry)
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Id)
                ? string.Join('|', x.BulletinNumber, x.BulletinDate, x.Page.ToString(CultureInfo.InvariantCulture), x.NoteNumber, Normalize(x.Subject), Normalize(x.NoteType))
                : x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        store.SignedFiles ??= new Dictionary<string, FurrielSignedFileInfo>(StringComparer.OrdinalIgnoreCase);
        if (store.SignedFiles.Comparer != StringComparer.OrdinalIgnoreCase)
            store.SignedFiles = new Dictionary<string, FurrielSignedFileInfo>(store.SignedFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in store.Files)
        {
            file.Lines ??= [];
            file.Mentions ??= [];
            file.Date = string.IsNullOrWhiteSpace(file.Date) ? "—" : file.Date;
            file.Title = string.IsNullOrWhiteSpace(file.Title) ? "Aditamento do Furriel" : file.Title;
            file.Bulletin = CanonicalFurrielBulletin(file.Bulletin, file.Date, file.OriginalName, file.StoredPath);
            if (string.IsNullOrWhiteSpace(file.Bulletin) || Regex.IsMatch(file.Bulletin, @"^20\d{2}$"))
                file.Bulletin = "—";
            NormalizeLegacyFurrielMentions(file, store.SubjectIndex);
        }
    }

    private static void PrepareCurrentIndex(FurrielIndexStore store)
    {
        // Um índice da versão atual já foi normalizado antes de ser salvo. Repetir aqui
        // milhares de comparações e expressões regulares em toda abertura fazia o módulo
        // parecer travado mesmo quando o cache de pesquisa já estava pronto.
        store.Files ??= [];
        store.SubjectIndex ??= [];
        store.SignedFiles ??= new Dictionary<string, FurrielSignedFileInfo>(StringComparer.OrdinalIgnoreCase);
        if (store.SignedFiles.Comparer != StringComparer.OrdinalIgnoreCase)
            store.SignedFiles = new Dictionary<string, FurrielSignedFileInfo>(store.SignedFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var file in store.Files)
        {
            file.Lines ??= [];
            file.Mentions ??= [];
        }
    }

    private static bool IsLegacyMentionLinkedOnlyByWarName(BulletinMentionItem mention)
    {
        if (!mention.IsDatabaseMatch && !mention.MilitaryId.HasValue) return false;
        var rawEvidence = string.Join(' ', mention.Subject, mention.NoteTitle, mention.NoteNumber, mention.SubjectNoteDisplay, mention.NoteText, mention.NoteExcerpt, mention.ConsequenceText);
        var rawNorm = Normalize(rawEvidence);
        var rawDigits = Digits(rawEvidence);
        var nameNorm = Normalize(mention.MentionedMilitaryName);
        var docs = Digits(string.Join(' ', mention.MentionedMilitaryCpf, mention.MentionedMilitaryPrecCp));
        var hasName = nameNorm.Length >= 6 && rawNorm.Contains(nameNorm, StringComparison.Ordinal);
        var hasDoc = docs.Length >= 6 && rawDigits.Contains(docs, StringComparison.Ordinal);
        // Registro vinculado ao cadastro, mas sem nome completo/CPF/Prec no PDF: quase sempre
        // falso positivo por nome de guerra. Não exibir nem levar para a Carteira.
        return !hasName && !hasDoc;
    }

    private static void NormalizeLegacyFurrielMentions(FurrielBulletinFile file, IReadOnlyList<FurrielSubjectIndexEntry> subjectIndex)
    {
        if (file.Mentions.Count == 0) return;

        var canonicalBulletin = CanonicalFurrielBulletin(file.Bulletin, file.Date, file.OriginalName, file.StoredPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<BulletinMentionItem>();

        foreach (var mention in file.Mentions)
        {
            if (IsLegacyMentionLinkedOnlyByWarName(mention)) continue;

            var official = FindOfficialSubjectIndexByNoteNumber(file, mention, subjectIndex)
                ?? FindOfficialSubjectIndexByLetterAnchor(file, mention, file.Lines ?? [], subjectIndex)
                ?? FindBestSubjectIndexEntry(subjectIndex, file, mention, FirstNonEmpty(mention.NoteText, mention.NoteExcerpt));
            if (official is not null && !IsTrustworthySubjectIndexEntry(official)) official = null;

            var profile = BuildSearchProfile(string.Empty, new FurrielMilitaryOption
            {
                Id = mention.MilitaryId ?? 0,
                Rank = mention.MentionedMilitaryRank,
                FullName = mention.MentionedMilitaryName,
                WarName = mention.MentionedMilitaryWarName,
                Cpf = mention.MentionedMilitaryCpf,
                PrecCp = mention.MentionedMilitaryPrecCp,
                Source = "Índice"
            });
            var improved = ResolveProfessionalMentionContext(file.Lines ?? [], mention, profile);
            var direct = ResolveFurrielTitleFromText(FirstNonEmpty(mention.NoteText, mention.NoteExcerpt));

            var subject = CleanDisplaySubject(FirstNonEmpty(direct.Subject, improved.Subject));
            var note = CleanDisplayNote(FirstNonEmpty(direct.Note, improved.NoteTitle), subject);
            if (!IsTrustworthyFurrielTitle(subject, note) && official is not null)
            {
                subject = CleanDisplaySubject(official.Subject);
                note = CleanDisplayNote(official.NoteType, subject);
            }
            if (!IsTrustworthyFurrielTitle(subject, note))
            {
                var storedSubject = CleanDisplaySubject(mention.Subject);
                var storedNote = CleanDisplayNote(mention.NoteTitle, storedSubject);
                if (IsTrustworthyFurrielTitle(storedSubject, storedNote))
                {
                    subject = storedSubject;
                    note = storedNote;
                }
                else
                {
                    subject = "Menção no ADT Furriel";
                    note = string.Empty;
                }
            }

            mention.Subject = subject;
            mention.NoteTitle = note;
            if (official is not null && !string.IsNullOrWhiteSpace(official.NoteNumber) &&
                FurrielTitlesCompatible(subject, official.Subject)) mention.NoteNumber = official.NoteNumber;
            mention.SubjectNoteDisplay = ComposeSubjectNote(subject, note, string.Empty);
            mention.BulletinNumber = canonicalBulletin;
            if (string.IsNullOrWhiteSpace(mention.SourceFilePath)) mention.SourceFilePath = file.StoredPath;
            mention.PageNumber = mention.PageNumber is > 0 ? mention.PageNumber : 1;

            var personKey = string.Join(' ', mention.MilitaryId?.ToString(CultureInfo.InvariantCulture), mention.MentionedMilitaryName, mention.MentionedMilitaryCpf, mention.MentionedMilitaryPrecCp);
            var key = string.Join('|',
                Normalize(personKey),
                Normalize(canonicalBulletin),
                Normalize(file.Bar),
                mention.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "1",
                Normalize(subject),
                Normalize(note));
            if (seen.Add(key)) normalized.Add(mention);
        }

        file.Mentions = normalized;
    }

    private static bool FurrielTitlesCompatible(string left, string right)
    {
        var leftNorm = Normalize(left);
        var rightNorm = Normalize(right);
        if (leftNorm.Length == 0 || rightNorm.Length == 0) return false;
        if (leftNorm == rightNorm || leftNorm.Contains(rightNorm, StringComparison.Ordinal) || rightNorm.Contains(leftNorm, StringComparison.Ordinal)) return true;
        var leftTokens = leftNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 5).ToHashSet(StringComparer.Ordinal);
        var rightTokens = rightNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => token.Length >= 5).ToHashSet(StringComparer.Ordinal);
        return leftTokens.Intersect(rightTokens).Any();
    }

    private FurrielBulletinFile ResolveSignedMetadata(FurrielIndexStore store, FurrielBulletinFile metadata, FurrielBulletinFile? selectedHint)
    {
        var number = BulletinNumber(metadata.Bulletin);
        if (selectedHint is not null && (number is null || BulletinNumber(selectedHint.Bulletin) == number)) return selectedHint;
        if (number is null)
            throw new InvalidOperationException("Não consegui identificar o número do Aditamento. Renomeie para algo como 'Adt 15 Ass.pdf' ou selecione o boletim comum antes de salvar.");

        var year = BulletinYear(metadata.Bulletin);
        var candidates = store.Files.Where(x => BulletinNumber(x.Bulletin) == number)
            .Where(x => string.IsNullOrWhiteSpace(year) || string.IsNullOrWhiteSpace(BulletinYear(x.Bulletin)) || BulletinYear(x.Bulletin) == year)
            .Where(x => string.IsNullOrWhiteSpace(metadata.Bar) || string.IsNullOrWhiteSpace(x.Bar) || Normalize(x.Bar) == Normalize(metadata.Bar))
            .ToList();
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1)
        {
            var sourceKey = SignedLinkKey(metadata.SourceOriginalName);
            var selected = candidates
                .Select(x => (Item: x, Score: CandidateScore(metadata, x, sourceKey)))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (selected.Item is not null && selected.Score >= 500) return selected.Item;
        }
        if (!metadata.Bulletin.Contains("????", StringComparison.Ordinal)) return metadata;
        return metadata;
    }

    private static int CandidateScore(FurrielBulletinFile signed, FurrielBulletinFile candidate, string signedKey)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(signed.SourceDirectory) && !string.IsNullOrWhiteSpace(candidate.SourceDirectory))
        {
            if (Path.GetFullPath(signed.SourceDirectory).Equals(Path.GetFullPath(candidate.SourceDirectory), StringComparison.OrdinalIgnoreCase)) score += 1200;
            else if (Path.GetDirectoryName(signed.SourceDirectory)?.Equals(Path.GetDirectoryName(candidate.SourceDirectory), StringComparison.OrdinalIgnoreCase) == true) score += 500;
        }
        var candidateKey = SignedLinkKey(candidate.SourceOriginalName.Length > 0 ? candidate.SourceOriginalName : candidate.OriginalName);
        if (signedKey.Length > 0 && candidateKey.Length > 0)
        {
            if (signedKey == candidateKey) score += 900;
            else if (signedKey.Contains(candidateKey, StringComparison.OrdinalIgnoreCase) || candidateKey.Contains(signedKey, StringComparison.OrdinalIgnoreCase)) score += 420;
        }
        if (ParseDate(signed.Date) is { } sd && ParseDate(candidate.Date) is { } cd && sd.Date == cd.Date) score += 700;
        if (!string.IsNullOrWhiteSpace(BulletinYear(signed.Bulletin)) && BulletinYear(signed.Bulletin) == BulletinYear(candidate.Bulletin)) score += 350;
        if (!string.IsNullOrWhiteSpace(signed.Bar) && Normalize(signed.Bar) == Normalize(candidate.Bar)) score += 250;
        return score;
    }

    private static FurrielBulletinFile ExtractMetadata(IReadOnlyList<string> pages, string path)
    {
        var start = string.Join('\n', pages.Take(2));
        var bulletin = "—";
        var bar = string.Empty;
        var title = "Aditamento do Furriel";
        var match = FurrielNumberRegex().Match(start);
        if (match.Success)
        {
            bulletin = match.Groups[1].Value;
            bar = match.Groups[2].Value;
            title = $"ADITAMENTO DO FURRIEL Nº {bulletin}{(string.IsNullOrWhiteSpace(bar) ? string.Empty : $" BAR {bar}")}";
        }
        else
        {
            match = ContinuationNumberRegex().Match(start);
            if (match.Success)
            {
                bulletin = $"{match.Groups[1].Value}/????";
                bar = match.Groups[2].Value;
            }
        }

        var date = "—";
        var longDate = LongDateRegex().Match(start);
        if (longDate.Success && MonthLookup.TryGetValue(Normalize(longDate.Groups[2].Value), out var month))
            date = $"{int.Parse(longDate.Groups[1].Value, PtBr):00}/{month:00}/{longDate.Groups[3].Value}";
        else
        {
            var slash = SlashDateRegex().Match(start);
            if (slash.Success) date = $"{slash.Groups[1].Value}/{slash.Groups[2].Value}/{slash.Groups[3].Value}";
        }

        var metadata = new FurrielBulletinFile { Bulletin = bulletin, Bar = bar, Date = date, Title = title, OriginalName = Path.GetFileName(path) };
        return MergeFilenameMetadata(metadata, path);
    }

    private static bool IsFurrielBulletinPdf(FurrielBulletinFile file)
    {
        var evidence = Normalize(string.Join(' ', file.Title, file.OriginalName, file.SourceOriginalName, Path.GetFileName(file.StoredPath), (file.Lines ?? []).Take(25).Select(x => x.Text)));
        if (evidence.Contains("aditamento do furriel", StringComparison.Ordinal) || evidence.Contains("adt furr", StringComparison.Ordinal)) return true;
        if (IsLikelyFurrielFileName(file.OriginalName) || IsLikelyFurrielFileName(file.StoredPath) || IsLikelyFurrielFileName(file.SourceOriginalName))
        {
            // Nome do arquivo ajuda, mas não deve aceitar BI comum: precisa haver pelo menos algum marcador de ADT/Furriel no texto ou no título.
            return evidence.Contains("furriel", StringComparison.Ordinal) || evidence.Contains("aditamento", StringComparison.Ordinal) || evidence.Contains("adt", StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsLikelyFurrielFileName(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return false;
        var name = Normalize(Path.GetFileNameWithoutExtension(pathOrName));
        return name.Contains("furriel", StringComparison.Ordinal)
               || name.Contains("adt furr", StringComparison.Ordinal)
               || name.Contains("aditamento do furriel", StringComparison.Ordinal);
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static FurrielBulletinFile MetadataFromFilename(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var flat = OneLine(name.Replace('_', ' ').Replace('-', ' ').Replace('.', ' '));
        var date = "—";
        var yearDetected = string.Empty;
        var match = FilenameYearDateRegex().Match(name);
        if (match.Success)
        {
            date = $"{int.Parse(match.Groups[3].Value, PtBr):00}/{int.Parse(match.Groups[2].Value, PtBr):00}/{match.Groups[1].Value}";
            yearDetected = match.Groups[1].Value;
        }
        else
        {
            match = FilenameBrDateRegex().Match(name);
            if (match.Success)
            {
                date = $"{int.Parse(match.Groups[1].Value, PtBr):00}/{int.Parse(match.Groups[2].Value, PtBr):00}/{match.Groups[3].Value}";
                yearDetected = match.Groups[3].Value;
            }
        }

        var bar = FilenameBarRegex().Match(flat) is { Success: true } barMatch ? int.Parse(barMatch.Groups[1].Value, PtBr).ToString(PtBr) : string.Empty;
        var number = string.Empty;
        var year = string.Empty;
        var numberYear = FilenameNumberYearRegex().Match(flat);
        if (numberYear.Success)
        {
            number = int.Parse(numberYear.Groups[1].Value, PtBr).ToString(PtBr);
            year = numberYear.Groups[2].Value;
        }
        if (number.Length == 0)
        {
            var adt = FilenameAdtNumberRegex().Match(flat);
            if (adt.Success) number = int.Parse(adt.Groups[1].Value, PtBr).ToString(PtBr);
        }
        if (number.Length == 0)
        {
            var reverse = FilenameNumberAdtRegex().Match(flat);
            if (reverse.Success) number = int.Parse(reverse.Groups[1].Value, PtBr).ToString(PtBr);
        }
        year = year.Length > 0 ? year : yearDetected;
        var bulletin = number.Length == 0 ? "—" : year.Length > 0 ? $"{number}/{year}" : $"{number}/????";
        return new FurrielBulletinFile
        {
            Bulletin = bulletin,
            Bar = bar,
            Date = date,
            Title = number.Length == 0 ? "Aditamento do Furriel" : $"ADITAMENTO DO FURRIEL Nº {bulletin}{(bar.Length == 0 ? string.Empty : $" BAR {bar}")}",
            OriginalName = Path.GetFileName(path),
            StoredPath = Path.GetFullPath(path)
        };
    }

    private static FurrielBulletinFile MergeFilenameMetadata(FurrielBulletinFile metadata, string path)
    {
        var fromName = MetadataFromFilename(path);
        if ((string.IsNullOrWhiteSpace(metadata.Bulletin) || metadata.Bulletin == "—") && fromName.Bulletin != "—")
        {
            metadata.Bulletin = fromName.Bulletin;
            metadata.Title = fromName.Title;
        }
        if (string.IsNullOrWhiteSpace(metadata.Bar) && !string.IsNullOrWhiteSpace(fromName.Bar)) metadata.Bar = fromName.Bar;
        if ((string.IsNullOrWhiteSpace(metadata.Date) || metadata.Date == "—") && fromName.Date != "—") metadata.Date = fromName.Date;
        return metadata;
    }

    private static bool MatchesPeriod(FurrielBulletinFile file, FurrielPeriodFilter? filter)
    {
        if (filter is null || (string.IsNullOrWhiteSpace(filter.Month) && string.IsNullOrWhiteSpace(filter.Year) && filter.Start is null && filter.End is null)) return true;
        var date = ParseDate(file.Date);
        if (date is null) return false;
        if (int.TryParse(filter.Month, out var month) && date.Value.Month != month) return false;
        if (int.TryParse(filter.Year, out var year) && date.Value.Year != year) return false;
        if (filter.Start is { } start && date.Value.Date < start.Date) return false;
        if (filter.End is { } end && date.Value.Date > end.Date) return false;
        return true;
    }

    private static bool MatchesBulletinFilter(FurrielBulletinFile file, string filter)
    {
        filter = OneLine(filter);
        if (filter.Length == 0) return true;
        var number = BulletinNumber(file.Bulletin);
        var year = ParseDate(file.Date)?.Year.ToString(PtBr) ?? BulletinYear(file.Bulletin);
        var fields = string.Join(' ', new[]
        {
            file.Bulletin, file.Bar, file.Date, file.Title, file.OriginalName, Path.GetFileName(file.StoredPath),
            number?.ToString(PtBr) ?? string.Empty, number is null ? string.Empty : number.Value.ToString("0000", PtBr),
            number is null || year.Length == 0 ? string.Empty : $"{number}/{year}", string.IsNullOrWhiteSpace(file.Bar) ? string.Empty : $"BAR {file.Bar}"
        });
        var normalizedFilter = Normalize(filter);
        var digitsFilter = Digits(filter);
        return Normalize(fields).Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase)
               || (digitsFilter.Length > 0 && Digits(fields).Contains(digitsFilter, StringComparison.OrdinalIgnoreCase));
    }

    private static SearchProfile BuildSearchProfile(string query, FurrielMilitaryOption? military)
    {
        var profile = new SearchProfile
        {
            Display = military?.DisplayLabel ?? query,
            FullName = military?.FullName ?? string.Empty,
            WarName = military?.WarName ?? string.Empty,
            FromDatabase = military is not null
        };
        if (military is not null)
        {
            var full = Normalize(military.FullName);
            if (full.Length >= 3) profile.Names.Add(full);
            // Militar escolhido no cadastro: o nome completo continua sendo a chave
            // principal. Para boletins antigos que registraram somente P/G + nome de
            // guerra, aceita esse par como alias seguro; nunca usa o nome de guerra só.
            var tokens = Tokens(military.FullName, 2);
            if (tokens.Count > 0) profile.TokenGroups.Add(tokens);
            var rankWarTokens = Tokens($"{military.Rank} {military.WarName}", 1);
            if (!string.IsNullOrWhiteSpace(military.WarName) && rankWarTokens.Count >= 2)
                profile.TokenGroups.Add(rankWarTokens);
            foreach (var value in new[] { military.Cpf, military.Identity, military.PrecCp })
            {
                var digits = Digits(value);
                if (digits.Length >= 5) profile.DigitValues.Add(digits);
            }
            profile.PdfSearchTerm = PdfViewerSearchTerm(military.FullName, military.WarName, query);
        }
        else
        {
            profile.QueryNormalized = Normalize(query);
            if (profile.QueryNormalized.Length >= 3) profile.Names.Add(profile.QueryNormalized);
            var tokens = Tokens(query);
            if (tokens.Count > 0) profile.TokenGroups.Add(tokens);
            var digits = Digits(query);
            if (digits.Length >= 5) profile.DigitValues.Add(digits);
            profile.PdfSearchTerm = PlainPdfSearchText(query);
        }
        profile.Names = profile.Names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        profile.DigitValues = profile.DigitValues.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return profile;
    }

    private static NoteContext ResolveProfessionalMentionContext(IReadOnlyList<FurrielIndexedLine> lines, BulletinMentionItem mention, SearchProfile profile)
    {
        if (lines.Count == 0)
        {
            var fallbackSubject = CleanDisplaySubject(mention.Subject);
            var fallbackNote = CleanDisplayNote(mention.NoteTitle, fallbackSubject);
            return new NoteContext
            {
                Subject = fallbackSubject,
                NoteTitle = fallbackNote,
                Preview = Truncate(mention.NoteExcerpt, 260),
                Context = Truncate(mention.NoteText, 1100),
                Page = mention.PageNumber ?? 1
            };
        }

        var page = mention.PageNumber ?? 0;
        var bestIndex = -1;
        var bestScore = int.MinValue;
        for (var i = 0; i < lines.Count; i++)
        {
            var row = lines[i];
            var score = 0;
            if (page <= 0 || row.Page == page) score += 120;
            else if (Math.Abs(row.Page - page) == 1) score += 20;
            if (MatchesProfile(row.Normalized, row.Digits, profile)) score += 220;
            if (IsNameLine(row.Text)) score += 80;
            if (!string.IsNullOrWhiteSpace(mention.MentionedMilitaryName) && Normalize(row.Text).Contains(Normalize(mention.MentionedMilitaryName), StringComparison.Ordinal)) score += 140;
            if (!string.IsNullOrWhiteSpace(mention.MentionedMilitaryWarName) && Regex.IsMatch(row.Normalized, $@"\b{Regex.Escape(Normalize(mention.MentionedMilitaryWarName))}\b")) score += 50;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        if (bestIndex < 0 || bestScore < 100)
            bestIndex = Math.Max(0, lines.ToList().FindIndex(x => page <= 0 || x.Page == page));
        if (bestIndex < 0) bestIndex = 0;

        var resolvedContext = ResolveNoteContext(lines, bestIndex, profile);
        var professionalTitle = ResolveFurrielTitleNear(lines, bestIndex);
        if (string.IsNullOrWhiteSpace(professionalTitle.Subject))
            professionalTitle = ResolveFurrielTitleFromText(FirstNonEmpty(resolvedContext.Context, resolvedContext.Preview, mention.NoteText, mention.SubjectNoteDisplay));

        var subject = CleanDisplaySubject(FirstNonEmpty(professionalTitle.Subject, resolvedContext.Subject, mention.Subject, lines[bestIndex].Subject));
        var split = SplitSubjectAndNote(subject);
        subject = split.Subject;
        if (IsGenericFurrielSubject(subject) && !string.IsNullOrWhiteSpace(professionalTitle.Subject))
            subject = professionalTitle.Subject;

        var noteTitle = FirstNonEmpty(professionalTitle.Note, split.Note, resolvedContext.NoteTitle, DetectNoteTypeAbovePerson(lines, bestIndex, subject), mention.NoteTitle);
        noteTitle = CleanDisplayNote(noteTitle, subject);

        return new NoteContext
        {
            Subject = subject,
            NoteTitle = noteTitle,
            Preview = resolvedContext.Preview,
            Context = resolvedContext.Context,
            Page = Math.Max(1, lines[bestIndex].Page)
        };
    }

    private static (string Subject, string Note) ResolveFurrielTitleNear(IReadOnlyList<FurrielIndexedLine> lines, int index)
    {
        if (lines.Count == 0) return (string.Empty, string.Empty);
        index = Math.Clamp(index, 0, lines.Count - 1);
        var first = Math.Max(0, index - 220);
        for (var i = index; i >= first; i--)
        {
            var text = CleanSpaces(lines[i].Text);
            if (text.Length == 0) continue;
            if (i < index && IsHardNoteBoundary(text) && index - i > 12) break;

            var title = TryParseFurrielTitle(text);
            if (!string.IsNullOrWhiteSpace(title.Subject)) return title;

            if (IsFurrielLetterAnchor(text))
            {
                var merged = text;
                for (var j = i + 1; j <= Math.Min(index, i + 3) && j < lines.Count; j++)
                {
                    var next = CleanSpaces(lines[j].Text);
                    if (next.Length == 0 || IsStructuralLine(next) || IsNameLine(next) || IsHardNoteBoundary(next)) break;
                    if (Regex.IsMatch(next, @"^(?:Seja|No requerimento|Em virtude|Tendo em vista|Conforme|Referente|O militar|A militar|Os militares|As militares)\b", RegexOptions.IgnoreCase)) break;
                    merged = CleanSpaces(merged + " " + next);
                    title = TryParseFurrielTitle(merged);
                    if (!string.IsNullOrWhiteSpace(title.Subject)) return title;
                }
            }
        }
        return (string.Empty, string.Empty);
    }

    private static (string Subject, string Note) ResolveFurrielTitleFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (string.Empty, string.Empty);
        var normalized = Regex.Replace(text.Replace('–', '-').Replace('—', '-'), @"\s+", " ").Trim();
        // Alguns PDFs perdem a letra da alínea na extração, mas preservam o título no
        // começo do bloco: "ASSUNTO - Tipo Seja...". Limite no primeiro marcador de
        // texto corrido para não transformar a fundamentação da nota em título.
        var leading = Regex.Match(normalized,
            @"^(?<title>.{4,260}?\s+-\s+.{3,120}?)(?=\s+(?:Seja|Tendo\s+em\s+vista|Em\s+consequ|No\s+requerimento|Conforme|Referente|O\s+militar|A\s+militar|Os\s+militares|As\s+militares)\b|$)",
            RegexOptions.IgnoreCase);
        if (leading.Success)
        {
            var leadingTitle = TryParseFurrielTitle(leading.Groups["title"].Value);
            if (!string.IsNullOrWhiteSpace(leadingTitle.Subject)) return leadingTitle;
        }
        var matches = Regex.Matches(normalized, @"(?:^|\s)[a-z]{1,2}\.\s+(?!\d)[^.]{4,180}?\s+-\s+[^.]{3,160}", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var title = TryParseFurrielTitle(match.Value.Trim());
            if (!string.IsNullOrWhiteSpace(title.Subject)) return title;
        }
        return TryParseFurrielTitle(normalized);
    }

    private static (string Subject, string Note) TryParseFurrielTitle(string value)
    {
        var clean = CleanSpaces(value).Replace('–', '-').Replace('—', '-');
        if (string.IsNullOrWhiteSpace(clean)) return (string.Empty, string.Empty);
        if (IsFurrielLetterAnchor(clean))
            clean = Regex.Replace(clean, @"^[a-z]{1,2}\.\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (clean.Length < 4 || clean.Length > 260) return (string.Empty, string.Empty);
        if (IsStructuralLine(clean) || IsNameLine(clean)) return (string.Empty, string.Empty);
        if (Regex.IsMatch(clean, @"^(?:Seja|No requerimento|Em consequ|Tendo em vista|Conforme|Referente|Virtude|Em virtude|O militar|A militar|Os militares|As militares)\b", RegexOptions.IgnoreCase)) return (string.Empty, string.Empty);
        if (Regex.IsMatch(clean, @"\b(?:Prec[- ]?CP|CPF|IDT|R\$|Valor\s+(?:Di[aá]rio|Total)|Banco|Ag[êe]ncia|Conta)\b", RegexOptions.IgnoreCase)) return (string.Empty, string.Empty);
        if (IsForbiddenFurrielFreeTextTitle(clean)) return (string.Empty, string.Empty);

        var cameFromLetterAnchor = IsFurrielLetterAnchor(value);
        var parts = Regex.Split(clean, @"\s+-\s+").Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (parts.Length >= 2 && (cameFromLetterAnchor || LooksLikeStandaloneNoteTitle(clean)))
        {
            var subject = CleanDisplaySubject(parts[0]);
            var note = CleanDisplayNote(string.Join(" - ", parts.Skip(1)), subject);
            if (!string.IsNullOrWhiteSpace(subject) && !IsGenericFurrielSubject(subject))
                return (subject, note);
        }

        if (LooksLikeStandaloneNoteTitle(clean) || cameFromLetterAnchor)
        {
            var subject = CleanDisplaySubject(clean);
            if (!IsGenericFurrielSubject(subject)) return (subject, string.Empty);
        }
        return (string.Empty, string.Empty);
    }

    private static bool IsForbiddenFurrielFreeTextTitle(string value)
    {
        var clean = CleanSpaces(value).Replace('–', '-').Replace('—', '-');
        if (string.IsNullOrWhiteSpace(clean)) return false;
        var normalized = Normalize(clean);
        if (Regex.IsMatch(clean, @"^[A-ZÁÀÂÃÉÊÍÓÔÕÚÇ][A-Za-zÁÀÂÃÉÊÍÓÔÕÚÇáàâãéêíóôõúç\s.'-]{2,80}\s+-\s*[A-Z]{2}\b")) return true;
        if (Regex.IsMatch(clean, @"\b(?:Prec[- ]?CP|CPF|IDT|identidade|R\$|Valor|Banco|Ag[êe]ncia|Conta|Filho\(a\)|Filha|Filho|Nascimento|Nascid[oa]|Matr[ií]cula|Soldo|Pensão\s+Militar|Pensao\s+Militar|Total\s+L[ií]quido|Desc\.?|Parcela)\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(clean, @"^(?:Seja|Tendo em vista|Em consequ|No requerimento|O militar|A militar|Os militares|As militares|Conforme|Referente|Relativo|Devido)\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(clean, @"\b(?:art\.?\s*\d+|inciso|al[ií]nea|MP\s*n[ºo°]?|Decreto\s+n[ºo°]?|em\s+favor\s+do\(s\)\s+militar|abaixo\s+relacionad[oa]s?|publicad[oa]\s+no\s+BI|publicado\s+nos\s+Bol)\b", RegexOptions.IgnoreCase)) return true;
        return normalized.Contains("filho a de", StringComparison.Ordinal)
            || normalized.Contains("belo horizonte mg", StringComparison.Ordinal)
            || normalized.Contains("agencia conta", StringComparison.Ordinal)
            || normalized.Contains("conta corrente", StringComparison.Ordinal);
    }

    private static bool IsGenericFurrielSubject(string value)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0
            || normalized is "pagamento pessoal" or "ocorrencia do militar no aditamento" or "assunto nao identificado" or "mencao" or "ferias"
            || normalized is "outros assuntos" or "assuntos gerais e administrativos";
    }

    private static bool IsTrustworthyFurrielTitle(string? subject, string? note)
    {
        var cleanSubject = CleanDisplaySubject(subject ?? string.Empty);
        var cleanNote = CleanDisplayNote(note ?? string.Empty, cleanSubject);
        var normalized = Normalize(cleanSubject);
        if (cleanSubject.Length < 4 || IsGenericFurrielSubject(cleanSubject)) return false;
        if (IsForbiddenFurrielFreeTextTitle(ComposeSubjectNote(cleanSubject, cleanNote, string.Empty))) return false;
        if (normalized is "saque" or "ordem" or "sem" or "para" or "pela" or "pelo" or "acima especificada" or "ordenador de despesas") return false;
        if (normalized.Contains("outros assuntos", StringComparison.Ordinal) || normalized == "pagamento pessoal" || normalized.EndsWith(" pagamento pessoal", StringComparison.Ordinal)) return false;
        if (Regex.IsMatch(cleanSubject, @"^(?:a|e|ao|aos|o|os|de|da|do|das|dos)\b", RegexOptions.IgnoreCase)) return false;
        return cleanSubject.Any(char.IsLetter);
    }

    public static string ResolveProfessionalSubjectNoteDisplay(string? noteText)
    {
        var title = ResolveFurrielTitleFromText(noteText ?? string.Empty);
        return IsTrustworthyFurrielTitle(title.Subject, title.Note)
            ? ComposeSubjectNote(title.Subject, title.Note, string.Empty)
            : string.Empty;
    }

    private static string DetectNoteTypeAbovePerson(IReadOnlyList<FurrielIndexedLine> lines, int index, string subject)
    {
        if (lines.Count == 0) return string.Empty;
        index = Math.Clamp(index, 0, lines.Count - 1);
        var subjectIndex = -1;
        for (var i = index; i >= Math.Max(0, index - 80); i--)
        {
            var text = CleanSpaces(lines[i].Text);
            if (IsHeadingLine(text))
            {
                subjectIndex = i;
                break;
            }
            if (i < index && IsHardNoteBoundary(text) && index - i > 8) break;
        }
        var first = subjectIndex >= 0 ? subjectIndex + 1 : Math.Max(0, index - 12);
        var note = string.Empty;
        for (var i = first; i < index && i <= first + 5; i++)
        {
            var candidate = CleanSpaces(lines[i].Text);
            if (!IsProfessionalNoteTypeLine(candidate, subject)) continue;
            note = candidate;
        }
        return note;
    }

    private static (string Subject, string Note) SplitSubjectAndNote(string value)
    {
        var clean = TrimFurrielNoteType(CleanDisplaySubject(value));
        var pieces = Regex.Split(clean, @"\s+[-–—]\s+")
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
        if (pieces.Length < 2) return (clean, string.Empty);
        var subject = string.Join(" - ", pieces.Take(pieces.Length - 1)).Trim();
        var note = TrimFurrielNoteType(pieces[^1].Trim());
        return IsProfessionalNoteTypeLine(note, subject) ? (subject, note) : (clean, string.Empty);
    }

    private static bool IsProfessionalNoteTypeLine(string value, string subject)
    {
        var clean = CleanSpaces(value);
        if (clean.Length is < 3 or > 130) return false;
        var normalized = Normalize(clean);
        if (normalized is "a" or "e" or "ao" or "sem" or "art" or "pe" or "saque") return false;
        if (Normalize(clean) == Normalize(subject)) return false;
        if (IsStructuralLine(clean) || IsNameLine(clean) || IsHeadingLine(clean)) return false;
        if (Regex.IsMatch(clean, @"\b(?:Continua[çc][ãa]o\s+do\s+Adt|Pag\s*n[ºo°]?|BAR\s+\d+)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(clean, @"\b(?:CPF|Prec[- ]?CP|IDT|R\$|Banco|Ag[êe]ncia|Conta|Valor|filho\(a\)|filho|filha|nascid[oa]|militar(?:es)?|relacionad[oa]s?|conforme|segue|abaixo)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(clean, @"^(?:Em consequ|Seja|Tendo em vista|No requerimento|Referente|Virtude|Em virtude|Conforme|Devido|Relativo)\b", RegexOptions.IgnoreCase)) return false;
        return true;
    }

    private static string ComposeSubjectNote(string subject, string note, string fallbackDisplay)
    {
        subject = CleanDisplaySubject(subject);
        note = CleanDisplayNote(note, subject);
        if (string.IsNullOrWhiteSpace(subject))
        {
            var fallback = CleanSpaces(fallbackDisplay);
            if (fallback.Contains('—')) return fallback;
            return string.IsNullOrWhiteSpace(note) ? fallback : note;
        }
        return string.IsNullOrWhiteSpace(note) ? subject : $"{subject} - {note}";
    }

    private static string CleanDisplaySubject(string value)
    {
        var clean = FormatNoteTitle(value);
        if (string.IsNullOrWhiteSpace(clean) || clean == "—") return string.Empty;
        clean = Regex.Replace(clean, @"\s+[-–—]\s*$", string.Empty).Trim();
        return clean;
    }

    private static string CleanDisplayNote(string value, string subject)
    {
        var clean = FormatNoteTitle(value);
        clean = TrimFurrielNoteType(clean);
        if (!IsProfessionalNoteTypeLine(clean, subject)) return string.Empty;
        return clean;
    }

    private static string TrimFurrielNoteType(string value)
    {
        var clean = CleanSpaces(value).Replace('–', '-').Replace('—', '-');
        if (string.IsNullOrWhiteSpace(clean)) return string.Empty;

        clean = Regex.Replace(clean,
            @"\s*\(?Continua[çc][ãa]o\s+do\s+Adt\s+Furr\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s+Pag\s*n[ºo°]?\s*\d+.*$", string.Empty, RegexOptions.IgnoreCase).Trim();

        // No Aditamento do Furriel, a linha da nota às vezes vem colada com a
        // justificativa do corpo: "Ordem de Saque - virtude de estar previsto...".
        // Para a grade/Carteira, mostrar somente o título profissional da nota.
        clean = Regex.Replace(clean,
            @"\s+-\s+(?:em\s+)?virtude\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean,
            @"\s+-\s+(?:tendo\s+em\s+vista|conforme|referente|relativo|devido|por\s+ter|por\s+estar|para\s+fins?|correspondente)\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean,
            @"\s+(?:em\s+)?virtude\s+de\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean,
            @"\s+(?:Seja|No requerimento|O militar|A militar|Os militares|As militares)\b.*$",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s+-\s+$", string.Empty).Trim();
        return clean[..Math.Min(120, clean.Length)];
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static FurrielSearchResult CanonicalizeSelectedMilitaryResult(FurrielSearchResult source, FurrielMilitaryOption? military)
    {
        var copy = CloneSearchResult(source);
        if (military is null) return copy;

        copy.Military = military.DisplayLabel;
        copy.FullName = military.FullName;
        copy.WarName = military.WarName;
        copy.PdfSearchTerm = FirstNonEmpty(military.FullName, source.PdfSearchTerm, source.Military);
        copy.MatchFromDatabase = true;
        return copy;
    }

    private static FurrielSearchResult CloneSearchResult(FurrielSearchResult source) => new()
    {
        Military = source.Military,
        FullName = source.FullName,
        WarName = source.WarName,
        PdfSearchTerm = source.PdfSearchTerm,
        MatchFromDatabase = source.MatchFromDatabase,
        Type = source.Type,
        Bulletin = source.Bulletin,
        Bar = source.Bar,
        Date = source.Date,
        Page = source.Page,
        Signed = source.Signed,
        Subject = source.Subject,
        Preview = source.Preview,
        Context = source.Context,
        FileName = source.FileName,
        PdfPath = source.PdfPath,
        SignedPdfPath = source.SignedPdfPath,
        SignedFileName = source.SignedFileName,
        IndexedAt = source.IndexedAt,
        SubjectNoteDisplay = source.SubjectNoteDisplay,
        NoteText = source.NoteText,
        HasConsequence = source.HasConsequence,
        IsFurrielConsequence = source.IsFurrielConsequence,
        ConsequenceText = source.ConsequenceText
    };

    private static bool MatchesProfile(string normalized, string digits, SearchProfile profile)
    {
        if (profile.DigitValues.Any(x => digits.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
        if (profile.Names.Any(x => x.Length >= 3 && normalized.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
        foreach (var group in profile.TokenGroups)
        {
            if (group.Count >= 3)
            {
                var hay = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (group.All(hay.Contains)) return true;
            }
            else if (group.All(x => normalized.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return profile.QueryNormalized.Length >= 3 && normalized.Contains(profile.QueryNormalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesDirectMilitaryResult(FurrielSearchResult result, SearchProfile profile)
    {
        // Para militar selecionado, valide somente campos que representam a pessoa
        // mencionada, não o contexto inteiro da nota. Isso mantém a pesquisa rápida
        // e elimina linhas genéricas como "De:", "Para:", valores e cabeçalhos.
        var directText = OneLine(string.Join(' ', new[]
        {
            result.Military,
            result.FullName,
            result.WarName,
            result.PdfSearchTerm
        }.Where(x => !string.IsNullOrWhiteSpace(x))));

        if (string.IsNullOrWhiteSpace(directText)) return false;

        var normalized = Normalize(directText);
        var digits = Digits(directText);
        if (profile.DigitValues.Any(x => digits.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
        if (profile.Names.Any(x => x.Length >= 3 && normalized.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;

        // Aceita P/G + nome de guerra como alternativa, mas não aceita somente posto
        // ou somente nome de guerra solto, que gerava muitos falsos positivos.
        foreach (var group in profile.TokenGroups)
        {
            if (group.Count < 2) continue;
            var hay = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (group.All(hay.Contains)) return true;
        }

        return false;
    }

    private static int BestMatchIndex(IReadOnlyList<FurrielIndexedLine> lines, int index, SearchProfile profile)
    {
        var start = Math.Max(0, index - 8);
        var end = Math.Min(lines.Count - 1, index + 8);
        var bestIndex = index;
        var bestScore = int.MinValue;
        for (var candidate = start; candidate <= end; candidate++)
        {
            var row = lines[candidate];
            if (!MatchesProfile(row.Normalized, row.Digits, profile)) continue;
            var score = 120 - Math.Abs(candidate - index);
            if (profile.DigitValues.Any(x => row.Digits.Contains(x, StringComparison.OrdinalIgnoreCase))) score += 90;
            if (IsNameLine(row.Text)) score += 80;
            if (profile.Names.Any(x => x.Length >= 3 && row.Normalized.Contains(x, StringComparison.OrdinalIgnoreCase))) score += 70;
            if (profile.TokenGroups.Any(group => group.Count > 0 && group.All(x => row.Normalized.Contains(x, StringComparison.OrdinalIgnoreCase)))) score += 50;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = candidate;
            }
        }
        return bestIndex;
    }

    private static NoteContext ResolveNoteContext(IReadOnlyList<FurrielIndexedLine> lines, int index, SearchProfile profile)
    {
        if (lines.Count == 0) return new NoteContext { Subject = "—", Preview = string.Empty, Context = string.Empty };
        index = Math.Clamp(index, 0, lines.Count - 1);

        var titleIndex = FindNoteTitleIndex(lines, index);
        var stored = CleanStoredSubject(lines[index].Subject);
        var subject = titleIndex >= 0 ? FormatNoteTitle(lines[titleIndex].Text) : stored;
        if (string.IsNullOrWhiteSpace(subject)) subject = "—";

        var start = titleIndex >= 0 ? titleIndex : Math.Max(0, index - 4);
        var end = FindNoteEndIndex(lines, start, index);
        var rows = CleanNoteRows(lines.Skip(start).Take(Math.Max(1, end - start + 1))).ToList();
        if (rows.Count == 0) rows = CleanNoteRows(ContextWindow(lines, index, 4, 8)).ToList();

        var full = OneLine(string.Join(' ', rows));
        var preview = Truncate(OneLine(string.Join(' ', rows.Take(8))), 260);
        if (string.IsNullOrWhiteSpace(preview)) preview = Truncate(full, 260);

        var context = BuildReviewContext(rows, lines, index, profile);
        var noteTitle = DetectNoteTypeAbovePerson(lines, index, subject);
        return new NoteContext
        {
            Subject = subject,
            NoteTitle = CleanDisplayNote(noteTitle, subject),
            Preview = preview,
            Context = string.IsNullOrWhiteSpace(context) ? preview : context,
            Page = Math.Max(1, lines[index].Page)
        };
    }

    private static int FindNoteTitleIndex(IReadOnlyList<FurrielIndexedLine> lines, int index)
    {
        var first = Math.Max(0, index - 160);
        for (var candidate = index; candidate >= first; candidate--)
        {
            var text = CleanSpaces(lines[candidate].Text);
            if (IsLikelyNoteTitle(text)) return candidate;
            if (candidate < index && IsHardNoteBoundary(text) && index - candidate > 8) break;
        }
        return -1;
    }

    private static int FindNoteEndIndex(IReadOnlyList<FurrielIndexedLine> lines, int start, int anchor)
    {
        var end = Math.Clamp(anchor, start, lines.Count - 1);
        for (var candidate = Math.Max(start + 1, anchor + 1); candidate < lines.Count; candidate++)
        {
            var text = CleanSpaces(lines[candidate].Text);
            if (candidate > anchor && IsHardNoteBoundary(text)) break;
            if (candidate > anchor && IsLikelyNoteTitle(text)) break;
            end = candidate;
            if (candidate - start >= 90) break;
        }
        return Math.Clamp(end, start, lines.Count - 1);
    }

    private static IEnumerable<string> CleanNoteRows(IEnumerable<FurrielIndexedLine> rows)
    {
        foreach (var row in rows)
        {
            var text = CleanSpaces(row.Text);
            if (text.Length == 0 || IsStructuralLine(text)) continue;
            yield return FormatContextLine(text);
        }
    }

    private static string BuildReviewContext(IReadOnlyList<string> noteRows, IReadOnlyList<FurrielIndexedLine> lines, int index, SearchProfile profile)
    {
        var full = OneLine(string.Join(' ', noteRows));
        if (full.Length <= 1100) return full;

        var start = OneLine(string.Join(' ', noteRows.Take(8)));
        var head = Truncate(start, 620);
        if (MatchesProfile(Normalize(head), Digits(head), profile)) return Truncate(full, 1100);

        var anchor = OneLine(string.Join(' ', CleanNoteRows(ContextWindow(lines, index, 2, 6))));
        if (string.IsNullOrWhiteSpace(anchor)) return Truncate(full, 1100);
        return Truncate($"{head} ... Ocorrência: {anchor}", 1100);
    }

    private static bool IsLikelyNoteTitle(string line)
    {
        var clean = CleanSpaces(line);
        if (clean.Length < 4 || clean.Length > 220) return false;
        if (IsStructuralLine(clean) || IsNameLine(clean)) return false;
        if (Regex.IsMatch(clean, @"\b(?:Prec[- ]?CP|CPF|IDT|R\$|Valor\s+(?:Di[aá]rio|Total)|Banco|Ag[êe]ncia|Conta)\b", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(clean, @"^(?:Em consequ|Seja\s+|No requerimento|Tendo em vista|Referente\b)", RegexOptions.IgnoreCase)) return false;
        if (IsFurrielLetterAnchor(clean)) return true;

        var normalized = Normalize(clean);
        if (normalized is "pagamento pessoal" or "assuntos gerais e administrativos" or "outros assuntos") return false;
        if (Regex.IsMatch(clean, @"\s[-–—]\s") && LooksLikeStandaloneNoteTitle(clean)) return true;
        return false;
    }

    private static bool LooksLikeStandaloneNoteTitle(string line)
    {
        var clean = CleanSpaces(line);
        if (clean.Contains(',')) return false;
        if (Regex.IsMatch(clean, @"\b(?:conforme|c[oó]digo|conclu[ií]do|referente|valor|m[eê]s|dias?|militar(?:es)?|publicado|favor|abaixo|relacionad[oa]s?|correspondente)\b", RegexOptions.IgnoreCase)) return false;
        var words = Regex.Matches(clean, @"[A-Za-zÁÀÂÃÉÊÍÓÔÕÚÇáàâãéêíóôõúç0-9]+").Count;
        return words is >= 2 and <= 10;
    }

    private static bool IsHardNoteBoundary(string line)
    {
        var clean = CleanSpaces(line);
        if (clean.Length == 0) return false;
        if (Regex.IsMatch(clean, @"^\d+ª\s+Parte\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(clean, @"^\d+\.\s+", RegexOptions.IgnoreCase)) return true;
        var normalized = Normalize(clean);
        return normalized is "justica" or "disciplina" or "servicos diarios" or "instrucao" or "assuntos gerais e administrativos";
    }

    private static bool IsStructuralLine(string line)
    {
        var clean = CleanSpaces(line);
        if (clean.Length == 0) return true;
        if (HeaderRegex().IsMatch(clean) || ContinuationHeaderRegex().IsMatch(clean)) return true;
        if (Regex.IsMatch(clean, @"^Pag\s+n[ºo°]\s*\d+", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(clean, @"^\([^)]+-feira\)$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(clean, @"^(?:BOLETIM\s+INTERNO|ADITAMENTO\s+DO\s+FURRIEL)\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(clean, @"^Para conhecimento deste aquartelamento", RegexOptions.IgnoreCase)) return true;
        return Normalize(clean) is "sem alteracao" or "sem alteracoes";
    }

    private static bool IsUsableStoredSubject(string value)
    {
        var clean = CleanSpaces(value);
        return clean.Length > 0 && clean != "—" && !IsStructuralLine(clean) && !IsNameLine(clean);
    }

    private static string CleanStoredSubject(string value)
    {
        var clean = FormatNoteTitle(value);
        return IsUsableStoredSubject(clean) ? clean : string.Empty;
    }

    private static string FormatContextLine(string line)
        => IsLikelyNoteTitle(line)
            ? FormatNoteTitle(line)
            : Regex.Replace(CleanSpaces(line).Replace('–', '-').Replace('—', '-'), @"\s+", " ").Trim();

    private static string FormatNoteTitle(string line)
    {
        var clean = CleanSpaces(line)
            .Replace('–', '-')
            .Replace('—', '-');
        if (IsFurrielLetterAnchor(clean))
            clean = Regex.Replace(clean, @"^[a-z]{1,2}\.\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s+-\s+", " - ").Trim();
        if (clean.Length == 0) return string.Empty;
        if (IsMostlyUppercase(clean))
        {
            clean = PtBr.TextInfo.ToTitleCase(clean.ToLower(PtBr));
            clean = LowerSmallWords(clean);
            clean = RestoreKnownAcronyms(clean);
        }
        return clean[..Math.Min(180, clean.Length)];
    }

    private static bool IsMostlyUppercase(string value)
    {
        var letters = value.Where(char.IsLetter).ToList();
        if (letters.Count < 4) return false;
        return letters.Count(char.IsUpper) >= letters.Count * 0.75;
    }

    private static string LowerSmallWords(string value)
    {
        foreach (var word in new[] { "De", "Da", "Do", "Das", "Dos", "E", "Ao", "Aos", "Em", "No", "Na" })
            value = Regex.Replace(value, $@"(?<!^)\b{word}\b", word.ToLower(PtBr), RegexOptions.CultureInvariant);
        return value;
    }

    private static string RestoreKnownAcronyms(string value)
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cpf"] = "CPF",
            ["Prec-Cp"] = "Prec-CP",
            ["Prec Cp"] = "Prec-CP",
            ["Idt"] = "IDT",
            ["Bi"] = "BI",
            ["Nr"] = "Nr",
            ["Pnr"] = "PNR",
            ["Ompe"] = "OMPE",
            ["Sippes"] = "SIPPES",
            ["Cpex"] = "CPEx"
        };
        foreach (var pair in replacements)
            value = Regex.Replace(value, $@"\b{Regex.Escape(pair.Key)}\b", pair.Value, RegexOptions.IgnoreCase);
        return value;
    }

    private static string Truncate(string value, int max)
    {
        value = OneLine(value);
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    private static List<FurrielIndexedLine> ContextWindow(IReadOnlyList<FurrielIndexedLine> lines, int index, int before, int after)
    {
        var start = Math.Max(0, index - before);
        var count = Math.Min(lines.Count - start, before + after + 1);
        return lines.Skip(start).Take(count).ToList();
    }

    private static string CompactContext(IEnumerable<FurrielIndexedLine> rows, int max = 900)
    {
        var text = OneLine(string.Join(' ', rows.Select(x => CleanSpaces(x.Text)).Where(x => x.Length > 0)));
        return text.Length > max ? text[..(max - 1)] + "…" : text;
    }

    private static string ExtractProbableName(IEnumerable<FurrielIndexedLine> rows, string fallback, SearchProfile profile)
    {
        foreach (var row in rows)
            if (IsNameLine(row.Text) && MatchesProfile(Normalize(row.Text), Digits(row.Text), profile)) return CleanSpaces(row.Text);
        return rows.Select(x => x.Text).FirstOrDefault(IsNameLine) ?? fallback;
    }

    private static string ClassifySubject(string subject, string context)
    {
        var text = Normalize(subject + " " + context);
        var checks = new (string Label, string[] Terms)[]
        {
            ("Domicílio bancário", ["domicilio bancario", "banco", "agencia", "conta"]),
            ("Exercício anterior", ["exercicio anterior", "despesa de exercicio anterior", "er0062"]),
            ("Compensação pecuniária", ["compensacao pecuniaria", "peculio"]),
            ("Férias", ["ferias", "adicional ferias"]),
            ("Pagamento", ["pagamento", "suspensao de pagamento", "saque", "remuneratorio"]),
            ("Licenciamento", ["licenciamento", "licenciado", "licenciada"]),
            ("Transferência", ["transferencia", "transferido", "transferida"]),
            ("Apresentação", ["apresentacao", "apresentou se", "apresentou"]),
            ("Alteração", ["alteracao", "alterado", "alterada"])
        };
        return checks.FirstOrDefault(x => x.Terms.Any(text.Contains)).Label ?? "Menção";
    }

    private static List<FurrielSearchResult> SortResults(List<FurrielSearchResult> results)
        => results.OrderBy(x => ParseDate(x.Date) ?? DateTime.MaxValue).ThenBy(x => BulletinNumber(x.Bulletin) ?? int.MaxValue).ThenBy(x => x.Page).ToList();

    private static string CleanPdfLine(string line)
    {
        line = CleanSpaces(line);
        if (line.Length == 0) return string.Empty;
        if (HeaderRegex().IsMatch(line) || ContinuationHeaderRegex().IsMatch(line)) return string.Empty;
        return Regex.IsMatch(line, @"^[\W_]+$", RegexOptions.CultureInvariant) ? string.Empty : line;
    }

    private static bool IsNameLine(string line)
    {
        var clean = CleanSpaces(line);
        var match = RankRegex().Match(clean);
        if (!match.Success) return false;
        if (clean.Length > match.Length && (clean[match.Length] == '/' || clean[match.Length] == ',')) return false;
        if (Regex.IsMatch(clean, @"\b(?:situad[oa]|bairro|apartamento|apto|rua|avenida|valor|conta|ag[êe]ncia)\b", RegexOptions.IgnoreCase)) return false;
        var rest = clean[match.Length..].Trim(' ', '-', ':');
        return Regex.Matches(rest, @"\b[A-ZÁÀÂÃÉÊÍÓÔÕÚÇ]{2,}\b").Count >= 1;
    }

    private static bool IsHeadingLine(string line)
    {
        var clean = CleanSpaces(line);
        if (clean.Length == 0 || IsNameLine(clean)) return false;
        var normalized = Normalize(clean);
        if (normalized is "sem alteracao" or "sem alteracoes") return false;
        if (Regex.IsMatch(clean, @"\b(?:Prec[- ]?CP|CPF|IDT)\s*[:\-]?", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(clean, @"^(Banco|Ag[êe]ncia|Conta)\s*:", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(clean, @"^(Em consequ|Seja\s+|No requerimento|Em virtude|Tendo em vista|Referente)", RegexOptions.IgnoreCase)) return false;
        if (Regex.IsMatch(clean, @"^\d+ª\s+Parte\b", RegexOptions.IgnoreCase) || Regex.IsMatch(clean, @"^\d+\.\s+") || IsFurrielLetterAnchor(clean)) return true;
        var letters = Regex.Replace(clean, @"[^A-ZÁÀÂÃÉÊÍÓÔÕÚÇ ]", string.Empty);
        var wordCount = letters.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(x => x.Length >= 2);
        var noise = Regex.IsMatch(clean, @"\b(?:R\$|VALOR|CORRIGIDO|RUBRICA|DESCRI[ÇC][ÃA]O|CPF|PREC|PER[ÍI]ODO DA D[ÍI]VIDA)\b", RegexOptions.IgnoreCase);
        return clean.Length >= 7 && wordCount >= 2 && !noise && letters.Length >= Math.Max(6, (int)(clean.Length * .65)) && clean == clean.ToUpper(PtBr);
    }

    private static string HeadingLabel(string line)
    {
        var value = CleanSpaces(line);
        value = Regex.Replace(value, @"^\d+ª\s+Parte\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        value = Regex.Replace(value, @"^\d+\.\s*", string.Empty).Trim();
        if (IsFurrielLetterAnchor(value))
            value = Regex.Replace(value, @"^[a-z]{1,2}\.\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        return value[..Math.Min(160, value.Length)];
    }

    private static string OrderedDestination(string source, FurrielBulletinFile file, string baseDirectory, bool signed)
    {
        var date = ParseDate(file.Date);
        var year = date?.Year.ToString(PtBr) ?? "sem_ano";
        var month = date?.Month.ToString("00", PtBr) ?? "sem_mes";
        var datePrefix = date?.ToString("yyyy-MM-dd", PtBr) ?? "sem_data";
        var number = BulletinNumber(file.Bulletin)?.ToString("0000", PtBr) ?? "0000";
        var bulletin = SafeFileName(file.Bulletin.Replace('/', '-'));
        var bar = string.IsNullOrWhiteSpace(file.Bar) ? string.Empty : "_BAR-" + SafeFileName(file.Bar);
        var suffix = signed ? "_(ASSINADO)" : string.Empty;
        var digest = (file.Id.Length >= 10 ? file.Id[..10] : HashFile(source)[..10]);
        return Path.Combine(baseDirectory, year, month, $"{datePrefix}_ADT_FURRIEL_{number}_{bulletin}{bar}{suffix}_{digest}.pdf");
    }

    private static IEnumerable<string> LookupKeys(FurrielBulletinFile file)
    {
        var result = new List<string>
        {
            StorageKey(file.Bulletin, file.Bar, file.Date),
            StorageKey(file.Bulletin, string.Empty, file.Date)
        };
        var compact = Normalize(file.Bulletin).Replace(" ", string.Empty);
        if (compact.Length > 0) result.Add(compact + "|bar:");
        var number = BulletinNumber(file.Bulletin);
        if (number is not null)
        {
            var year = BulletinYear(file.Bulletin);
            var bar = Normalize(file.Bar);
            if (year.Length > 0) result.Add($"nr:{number:0000}|ano:{year}|bar:{bar}");
            result.Add($"nr:{number:0000}|bar:{bar}");
            if (bar.Length > 0) result.Add($"nr:{number:0000}|bar:");
        }
        return result.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string StorageKey(string bulletin, string bar, string date)
    {
        var compact = Normalize(bulletin).Replace(" ", string.Empty);
        if (compact.Contains("????", StringComparison.Ordinal) && ParseDate(date) is { } parsed)
            compact = compact.Replace("????", parsed.Year.ToString(PtBr), StringComparison.Ordinal);
        return $"{compact}|bar:{Normalize(bar)}";
    }

    private static bool IsSignedByName(string path)
    {
        var normalized = Normalize(Path.GetFileNameWithoutExtension(path));
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tokens.Any(x => x is "ass" or "assinado" or "assinada" or "assinados" or "assinadas" or "signed");
    }

    private static string SignedLinkKey(string value)
    {
        var tokens = Normalize(Path.GetFileNameWithoutExtension(value)).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x is not ("ass" or "assinado" or "assinada" or "assinados" or "assinadas" or "signed"));
        return Regex.Replace(string.Join(' ', tokens), @"\b0+(\d{1,4})\b", "$1").Trim();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string((value ?? string.Empty).Select(x => invalid.Contains(x) ? '_' : x).ToArray());
        clean = Regex.Replace(clean, @"\s+", "_").Trim(' ', '.', '_');
        return clean.Length == 0 ? "item" : clean;
    }

    private static string CanonicalFurrielBulletin(string? bulletin, string? date, string? originalName, string? storedPath)
    {
        var cleanBulletin = OneLine(bulletin ?? string.Empty);
        var inferred = MetadataFromFilename(!string.IsNullOrWhiteSpace(originalName) ? originalName! : storedPath ?? string.Empty);
        var number = BulletinNumber(cleanBulletin);
        var inferredNumber = BulletinNumber(inferred.Bulletin);

        // Quando um índice antigo gravou apenas "2026" no campo do boletim, isso é ano,
        // não número do ADT. Nessa situação, recupera o número correto pelo nome do arquivo.
        if (number.HasValue && number.Value >= 1900 && number.Value <= 2099 && inferredNumber is not null && inferredNumber.Value != number.Value)
            number = inferredNumber;
        else if (!number.HasValue && inferredNumber is not null)
            number = inferredNumber;

        var year = ParseDate(date ?? string.Empty)?.Year.ToString(PtBr) ?? BulletinYear(cleanBulletin);
        if (string.IsNullOrWhiteSpace(year) || year.Contains("?", StringComparison.Ordinal))
            year = BulletinYear(inferred.Bulletin);
        if ((string.IsNullOrWhiteSpace(year) || year.Contains("?", StringComparison.Ordinal)) && ParseDate(inferred.Date) is { } inferredDate)
            year = inferredDate.Year.ToString(PtBr);

        if (number is not null && !string.IsNullOrWhiteSpace(year))
            return $"{number.Value.ToString(PtBr)}/{year}";
        return CleanBulletinNumber(cleanBulletin);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant()[..24];

    private static string FileOrderKey(FurrielBulletinFile file)
    {
        var date = ParseDate(file.Date)?.ToString("yyyyMMdd", PtBr) ?? "99999999";
        var number = BulletinNumber(file.Bulletin)?.ToString("0000", PtBr) ?? "9999";
        return $"{date}|{number}|{file.Bar}|{file.OriginalName}";
    }

    private static int? BulletinNumber(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\d+");
        return match.Success && int.TryParse(match.Value, out var number) ? number : null;
    }

    private static string CleanBulletinNumber(string value) => BulletinNumber(value)?.ToString(PtBr) ?? OneLine(value);
    private static string BulletinYear(string value) => Regex.Match(value ?? string.Empty, @"(?:^|/)\s*(20\d{2})\b") is { Success: true } m ? m.Groups[1].Value : string.Empty;

    private static DateTime? ParseDate(string value)
    {
        foreach (var format in new[] { "dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd" })
            if (DateTime.TryParseExact(OneLine(value), format, PtBr, DateTimeStyles.None, out var date)) return date;
        return null;
    }

    private static string NormalizePersonnelSource(string value)
        => value is SourceActive or SourceAll or SourceInactive ? value : SourceActive;

    private static string BuildMilitaryIdentity(FurrielMilitaryOption item)
    {
        var cpf = Digits(item.Cpf);
        if (cpf.Length > 0) return "cpf:" + cpf;
        var id = Digits(item.Identity);
        if (id.Length > 0) return "idt:" + id;
        return Normalize(item.FullName + "|" + item.Rank + "|" + item.Source);
    }

    private static int ScoreMilitary(FurrielMilitaryOption item, string query, string queryDigits)
    {
        var name = Normalize(item.FullName);
        var war = Normalize(item.WarName);
        var label = Normalize(item.DisplayLabel);
        var digits = Digits(item.Cpf + item.Identity + item.PrecCp);
        var score = 0;
        if (queryDigits.Length >= 4 && digits.Contains(queryDigits, StringComparison.OrdinalIgnoreCase)) score += 1200;
        if (war == query) score += 1000;
        else if (war.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 850;
        else if (war.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 700;
        if (name == query) score += 950;
        else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 800;
        else if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 650;
        if (label.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 250;
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 1 && tokens.All(x => label.Contains(x, StringComparison.OrdinalIgnoreCase))) score += 500;
        return score;
    }

    private static List<string> Tokens(string text, int minimumLength = 3)
        => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= minimumLength && !StopWords.Contains(x)).ToList();

    private static string PdfViewerSearchTerm(string fullName, string warName, string fallback)
    {
        var complete = PlainPdfSearchText(fullName);
        if (!string.IsNullOrWhiteSpace(complete)) return complete;
        return PlainPdfSearchText(!string.IsNullOrWhiteSpace(warName) ? warName : fallback);
    }

    private static string PlainPdfSearchText(string value)
    {
        value = Regex.Replace(value ?? string.Empty, @"[\u200b-\u200f\ufeff]", string.Empty);
        value = OneLine(value);
        return value.Length > 130 ? value[..130] : value;
    }

    private static string CleanSpaces(string value)
    {
        value = (value ?? string.Empty).Replace('\u00a0', ' ');
        value = Regex.Replace(value, @"[ \t]+", " ");
        value = Regex.Replace(value, @"\n{3,}", "\n\n");
        return value.Trim();
    }

    private static string OneLine(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static string Digits(string? value) => Regex.Replace(value ?? string.Empty, @"\D+", string.Empty);
    private static string MonthAbbreviation(int month) => new[] { "JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ" }[month - 1];

    private static string? FindExecutable(string name)
    {
        if (Path.IsPathRooted(name) && File.Exists(name)) return name;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(directory.Trim(), name);
                if (File.Exists(path)) return path;
            }
            catch { }
        }
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "tools"), Path.Combine(AppContext.BaseDirectory, "poppler", "Library", "bin") })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private void CleanupTempFolders()
    {
        try
        {
            if (!Directory.Exists(TempDirectory)) return;
            foreach (var directory in Directory.EnumerateDirectories(TempDirectory, "furriel_zip_*"))
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        }
        catch { }
    }

    private static string ExtractPdfTextBestEffort(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.Latin1.GetString(bytes);
            var builder = new StringBuilder();
            foreach (Match match in Regex.Matches(text, "\\((?<v>(?:\\\\.|[^\\\\)])*)\\)\\s*(?:Tj|'|\")", RegexOptions.Singleline))
                builder.AppendLine(DecodePdfLiteral(match.Groups["v"].Value));
            foreach (Match array in Regex.Matches(text, @"\[(?<v>.*?)\]\s*TJ", RegexOptions.Singleline))
                foreach (Match item in Regex.Matches(array.Groups["v"].Value, @"\((?<v>(?:\\.|[^\\)])*)\)")) builder.Append(DecodePdfLiteral(item.Groups["v"].Value));
            return builder.ToString();
        }
        catch { return string.Empty; }
    }

    private static string DecodePdfLiteral(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\\([nrtbf()\\]|[0-7]{1,3})", match =>
        {
            var token = match.Groups[1].Value;
            return token switch
            {
                "n" => "\n", "r" => "\r", "t" => "\t", "b" => "\b", "f" => "\f",
                "(" => "(", ")" => ")", "\\" => "\\",
                _ when token.All(x => x is >= '0' and <= '7') => ((char)Convert.ToInt32(token, 8)).ToString(),
                _ => token
            };
        });
    }

    private static void SendCtrlFAndPaste()
    {
        if (!OperatingSystem.IsWindows()) return;
        const byte control = 0x11;
        const byte f = 0x46;
        const byte a = 0x41;
        const byte v = 0x56;
        const uint keyUp = 0x0002;
        static void Down(byte key) => keybd_event(key, 0, 0, UIntPtr.Zero);
        static void Up(byte key) => keybd_event(key, 0, keyUp, UIntPtr.Zero);
        static void HotKey(byte key)
        {
            Down(control); Thread.Sleep(35); Down(key); Thread.Sleep(35); Up(key); Thread.Sleep(35); Up(control);
        }
        try
        {
            HotKey(f); Thread.Sleep(150); HotKey(a); Thread.Sleep(70); HotKey(v);
        }
        catch { }
    }

    private static void PressKey(byte key)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            const uint keyUp = 0x0002;
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            keybd_event(key, 0, keyUp, UIntPtr.Zero);
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private sealed class SearchProfile
    {
        public string Display { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string WarName { get; set; } = string.Empty;
        public string PdfSearchTerm { get; set; } = string.Empty;
        public bool FromDatabase { get; set; }
        public List<string> Names { get; set; } = [];
        public List<string> DigitValues { get; set; } = [];
        public List<List<string>> TokenGroups { get; } = [];
        public string QueryNormalized { get; set; } = string.Empty;
    }

    private sealed class NoteContext
    {
        public string Subject { get; set; } = "—";
        public string NoteTitle { get; set; } = string.Empty;
        public string Preview { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
        public int Page { get; set; }
    }

    [GeneratedRegex(@"ADITAMENTO\s+DO\s+FURRIEL\s+N[ºo°]?\s*(\d{1,4}/\d{4})(?:\s+BAR\s*(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FurrielNumberRegex();
    [GeneratedRegex(@"Adt\s+Furr\s+Nr\s+(\d+)\s+BAR\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContinuationNumberRegex();
    [GeneratedRegex(@"Belo\s+Horizonte/MG,\s*(\d{1,2})\s+de\s+([A-Za-zçÇéÉãÃ]+)\s+de\s+(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex LongDateRegex();
    [GeneratedRegex(@"\b(\d{2})/(\d{2})/(\d{4})\b")]
    private static partial Regex SlashDateRegex();
    [GeneratedRegex(@"(?<!\d)(20\d{2})[-_. ](\d{1,2})[-_. ](\d{1,2})(?!\d)")]
    private static partial Regex FilenameYearDateRegex();
    [GeneratedRegex(@"(?<!\d)(\d{1,2})[-_. ](\d{1,2})[-_. ](20\d{2})(?!\d)")]
    private static partial Regex FilenameBrDateRegex();
    [GeneratedRegex(@"(?<!\d)(\d{1,4})\s*[/\-_. ]\s*(20\d{2})(?!\d)")]
    private static partial Regex FilenameNumberYearRegex();
    [GeneratedRegex(@"(?:\badt\b|\baditamento\b|\bfurr(?:iel)?\b|\bnr\b|\bnum(?:ero)?\b|\bn[ºo°]?\b)[^0-9]{0,25}0*(\d{1,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex FilenameAdtNumberRegex();
    [GeneratedRegex(@"(?<!\d)0*(\d{1,4})(?!\d)[^A-Za-z0-9]{0,20}(?:adt|aditamento|furr(?:iel)?)", RegexOptions.IgnoreCase)]
    private static partial Regex FilenameNumberAdtRegex();
    [GeneratedRegex(@"\bbar\s*0*(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FilenameBarRegex();
    [GeneratedRegex(@"^(?:MINIST[ÉE]RIO DA DEFESA|EX[ÉE]RCITO BRASILEIRO|CML\s*-\s*4ª\s*RM|4ª\s+COMPANHIA DE POL[ÍI]CIA DO EX[ÉE]RCITO|Pel\s+Pol\s+QGR|Quartel\s+Rua)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderRegex();
    [GeneratedRegex(@"^\(Continua[çc][ãa]o\s+do\s+Adt\s+Furr", RegexOptions.IgnoreCase)]
    private static partial Regex ContinuationHeaderRegex();
    [GeneratedRegex(@"^(?:S\s*Ten|Sub\s*Ten|[1-3]º?\s*Sgt|[1-2]º?\s*Ten|Asp(?:\s*Of)?|Cap|Maj|Ten(?:\s*Cel)?|Cel|Cb(?:\s*Ef\s*(?:Profl|Vrv))?|Sd(?:\s*(?:EV|Ef\s*(?:Profl|Vrv)))?|Ex[- ]?militar)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RankRegex();
}

public sealed class FurrielPersistentSearchIndex
{
    public string Signature { get; set; } = string.Empty;
    public string BuiltAt { get; set; } = string.Empty;
    public List<FurrielPersistentSearchRow> Rows { get; set; } = [];
}

public sealed class FurrielPersistentSearchRow
{
    public string FileId { get; set; } = string.Empty;
    public string SearchNormalized { get; set; } = string.Empty;
    public string SearchDigits { get; set; } = string.Empty;
    public FurrielSearchResult Result { get; set; } = new();
}
