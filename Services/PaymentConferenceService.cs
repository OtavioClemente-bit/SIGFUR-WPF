using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Conferência profissional entre Aditamentos do Furriel e contracheques CPEx/SIPPES.
/// Lê publicações, preserva identidades e compara códigos explícitos ou regras do manual
/// com a identidade, competência e valores efetivamente extraídos dos PDFs selecionados.
/// </summary>
public sealed partial class PaymentConferenceService
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly object PdfSearchSync = new();
    private static CancellationTokenSource? _pdfSearchCancellation;
    public const string ParserVersion = "2026-09-09-monthly-audit-v6";
    private readonly AppPaths _paths;
    private readonly JsonFileService _json;
    private readonly PdfTextService _pdfText;
    private readonly MilitaryRepository _repository;
    private readonly LicensedTransferredRepository _licensedTransferred;
    private readonly PaystubService _paystubs;
    private readonly LogService _log;

    public PaymentConferenceService(AppPaths paths, JsonFileService json, PdfTextService pdfText, MilitaryRepository repository, LicensedTransferredRepository licensedTransferred, PaystubService paystubs, LogService log)
    {
        _paths = paths;
        _json = json;
        _pdfText = pdfText;
        _repository = repository;
        _licensedTransferred = licensedTransferred;
        _paystubs = paystubs;
        _log = log;
        Directory.CreateDirectory(ModuleDirectory);
        Directory.CreateDirectory(ReportsDirectory);
        Directory.CreateDirectory(_paths.PaymentConferenceCacheDirectory);
    }

    public string ModuleDirectory => Path.Combine(_paths.DataDirectory, "conferencia_pagamento");
    public string ReportsDirectory => Path.Combine(ModuleDirectory, "relatorios");
    public string SettingsFile => Path.Combine(ModuleDirectory, "config.json");
    public string LastCsvFile => Path.Combine(ReportsDirectory, $"conferencia_pagamento_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

    public async Task<PaymentConferenceSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _json.LoadAsync<PaymentConferenceSettings>(SettingsFile) ?? new PaymentConferenceSettings();
        settings.PaystubFolder = ResolvePaystubFolder(settings.PaystubFolder);
        return settings;
    }

    public string ResolvePaystubFolder(string? value)
    {
        var folder = string.IsNullOrWhiteSpace(value) ? _paths.PaystubsDirectory : value.Trim().Trim('"');
        // Repair accidental digits before an absolute drive path only when that exact folder exists.
        var accidentalPrefix = Regex.Match(folder, @"^\d+(?<path>[A-Za-z]:[\\/].+)$");
        if (!Directory.Exists(folder) && accidentalPrefix.Success && Directory.Exists(accidentalPrefix.Groups["path"].Value))
            folder = accidentalPrefix.Groups["path"].Value;
        return folder;
    }

    public Task SaveSettingsAsync(PaymentConferenceSettings settings, CancellationToken cancellationToken = default)
        => _json.SaveAsync(SettingsFile, settings);

    public async Task<string> SaveCacheAsync(
        PaymentConferenceResult result,
        IEnumerable<string> bulletinPaths,
        PaymentConferenceSettings settings,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.PaymentConferenceCacheDirectory);
        var selected = NormalizePaths(bulletinPaths);
        var signature = await BuildConferenceSignatureAsync(selected, settings, cancellationToken);
        if (result.InputSignature.Length == 0 || result.InputSignature != signature)
            throw new InvalidOperationException("Os arquivos ou cadastros mudaram desde a leitura. Refaça a conferência antes de salvar o resultado.");
        var cache = new PaymentConferenceCacheStore
        {
            Kind = "payment-conference",
            ParserVersion = ParserVersion,
            SavedAt = DateTime.Now,
            Settings = settings,
            BulletinPaths = selected,
            Signature = signature,
            Result = result
        };
        var path = CachePath(settings, selected);
        await _json.SaveAsync(path, cache);
        return path;
    }

    public async Task<PaymentConferenceCacheLoad?> LoadValidCacheAsync(
        IEnumerable<string> bulletinPaths,
        PaymentConferenceSettings settings,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.PaymentConferenceCacheDirectory);
        var selected = NormalizePaths(bulletinPaths);
        var expectedPath = CachePath(settings, selected);
        var candidates = File.Exists(expectedPath)
            ? [expectedPath]
            : Directory.EnumerateFiles(_paths.PaymentConferenceCacheDirectory, $"conference_{settings.Year}_{settings.Month:00}_*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        var signature = await BuildConferenceSignatureAsync(selected, settings, cancellationToken);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cache = await _json.LoadAsync<PaymentConferenceCacheStore>(candidate);
            if (cache is null || cache.Kind != "payment-conference") continue;
            if (!cache.ParserVersion.Equals(ParserVersion, StringComparison.OrdinalIgnoreCase)) continue;
            if (cache.Settings.Month != settings.Month || cache.Settings.Year != settings.Year) continue;
            if (!PathEquals(cache.Settings.PaystubFolder, settings.PaystubFolder)) continue;
            if (!SettingsSignature(cache.Settings).Equals(SettingsSignature(settings), StringComparison.OrdinalIgnoreCase)) continue;
            if (!SamePathSet(cache.BulletinPaths, selected)) continue;
            if (!cache.Signature.Equals(signature, StringComparison.OrdinalIgnoreCase)) continue;
            await ApplyVerificationsAsync(cache.Result);
            return new PaymentConferenceCacheLoad(cache.SavedAt, cache.Result, candidate);
        }
        return null;
    }

    public async Task<IReadOnlyList<PaymentConferenceBulletinFile>> LoadFurrielBulletinsAsync(CancellationToken cancellationToken = default)
    {
        var store = await _json.LoadAsync<FurrielIndexStore>(_paths.FurrielIndexFile) ?? new FurrielIndexStore();
        var result = new Dictionary<string, PaymentConferenceBulletinFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in store.Files ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveExistingFurrielPath(file.StoredPath) ?? ResolveListedFurrielPath(file.StoredPath);
            if (string.IsNullOrWhiteSpace(path)) continue;
            var metadata = MetadataFromFurrielPath(path);
            AddBulletin(result, new PaymentConferenceBulletinFile
            {
                Id = string.IsNullOrWhiteSpace(file.Id) ? HashText(path)[..16] : file.Id,
                Bulletin = string.IsNullOrWhiteSpace(file.Bulletin) ? metadata.Bulletin : file.Bulletin,
                Bar = string.IsNullOrWhiteSpace(file.Bar) ? metadata.Bar : file.Bar,
                Date = string.IsNullOrWhiteSpace(file.Date) ? metadata.Date : file.Date,
                OriginalName = string.IsNullOrWhiteSpace(file.OriginalName) ? Path.GetFileName(path) : file.OriginalName,
                Path = path,
                Source = "Índice do Aditamento Furriel",
                Pages = file.Pages,
                Status = File.Exists(path) ? "Pronto" : "PDF ausente"
            });
        }

        foreach (var path in EnumerateFurrielPdfCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = MetadataFromFurrielPath(path);
            AddBulletin(result, new PaymentConferenceBulletinFile
            {
                Id = HashText(Path.GetFullPath(path))[..16],
                Bulletin = metadata.Bulletin,
                Bar = metadata.Bar,
                Date = metadata.Date,
                OriginalName = Path.GetFileName(path),
                Path = Path.GetFullPath(path),
                Source = "Pasta de dados do Aditamento Furriel",
                Pages = 0,
                Status = "PDF localizado"
            });
        }

        return result.Values
            .OrderBy(x => ParseDate(x.Date))
            .ThenByDescending(x => ToInt(x.Bulletin))
            .ThenBy(x => x.OriginalName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<PaymentConferenceBulletinFile> BuildFileFromPdfAsync(string path, CancellationToken cancellationToken = default)
    {
        var pages = await _pdfText.ExtractPagesAsync(path, cancellationToken);
        var metadata = ExtractBulletinMetadata(pages, path);
        var items = ParseExpectedItems(path, pages, null, cancellationToken);
        return new PaymentConferenceBulletinFile
        {
            Id = HashText(Path.GetFullPath(path))[..16],
            Bulletin = metadata.Bulletin,
            Bar = metadata.Bar,
            Date = metadata.Date,
            OriginalName = Path.GetFileName(path),
            Path = path,
            Source = "PDF avulso",
            Pages = pages.Count,
            ExpectedItems = items.Count,
            Status = items.Count > 0 ? "Itens encontrados" : "Sem pagamento identificado"
        };
    }

    private void AddBulletin(Dictionary<string, PaymentConferenceBulletinFile> result, PaymentConferenceBulletinFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path)) return;
        var key = BulletinFileKey(file);
        if (result.TryGetValue(key, out var existing))
        {
            if (existing.Pages <= 0 && file.Pages > 0) result[key] = file;
            return;
        }
        result[key] = file;
    }

    private static string BulletinFileKey(PaymentConferenceBulletinFile file)
    {
        var length = SafeFileLength(file.Path);
        var identity = string.Join("|", file.Bulletin, file.Date, length);
        return string.IsNullOrWhiteSpace(identity.Replace("|", string.Empty, StringComparison.Ordinal))
            ? Path.GetFullPath(file.Path)
            : Normalize(identity);
    }

    private string? ResolveExistingFurrielPath(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;
        try
        {
            var module = Path.GetDirectoryName(_paths.FurrielIndexFile) ?? Path.Combine(_paths.DataDirectory, "boletim_furriel");
            var full = Path.IsPathRooted(storedPath)
                ? Path.GetFullPath(storedPath)
                : Path.GetFullPath(Path.Combine(module, storedPath));
            if (File.Exists(full)) return full;

            var name = Path.GetFileName(storedPath);
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var root in FurrielCandidateRoots())
            {
                try
                {
                    var match = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(match) && File.Exists(match)) return Path.GetFullPath(match);
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private string? ResolveListedFurrielPath(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;
        try
        {
            var module = Path.GetDirectoryName(_paths.FurrielIndexFile) ?? Path.Combine(_paths.DataDirectory, "boletim_furriel");
            return Path.GetFullPath(Path.IsPathRooted(storedPath) ? storedPath : Path.Combine(module, storedPath));
        }
        catch { return storedPath; }
    }

    private IEnumerable<string> EnumerateFurrielPdfCandidates()
    {
        foreach (var root in FurrielCandidateRoots())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var file in files)
            {
                var name = Normalize(Path.GetFileName(file));
                if (name.Contains("ADITAMENTO", StringComparison.Ordinal) || name.Contains("ADT", StringComparison.Ordinal) || name.Contains("FURRIEL", StringComparison.Ordinal))
                    yield return Path.GetFullPath(file);
            }
        }
    }

    private IEnumerable<string> FurrielCandidateRoots()
    {
        var module = Path.GetDirectoryName(_paths.FurrielIndexFile) ?? Path.Combine(_paths.DataDirectory, "boletim_furriel");
        foreach (var path in new[]
                 {
                     Path.Combine(module, "pdfs"),
                     Path.Combine(module, "tmp", "sisbol_aditamento_furriel"),
                     Path.Combine(module, "assinados"),
                     module
                 })
        {
            if (Directory.Exists(path)) yield return path;
        }
    }

    private static (string Bulletin, string Bar, string Date) MetadataFromFurrielPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var normalized = Normalize(name.Replace('_', ' ').Replace('-', ' '));
        var date = Regex.Match(name, @"(?<y>20\d{2})[-_](?<m>\d{2})[-_](?<d>\d{2})");
        var dateText = date.Success
            ? $"{date.Groups["d"].Value}/{date.Groups["m"].Value}/{date.Groups["y"].Value}"
            : "—";
        var bulletin = Regex.Match(name, @"ADT_FURRIEL_0*(?<n>\d{1,4})", RegexOptions.IgnoreCase).Groups["n"].Value;
        if (string.IsNullOrWhiteSpace(bulletin))
            bulletin = Regex.Match(name, @"_[NO]_0*(?<n>\d{1,4})_aditamento", RegexOptions.IgnoreCase).Groups["n"].Value;
        if (string.IsNullOrWhiteSpace(bulletin))
            bulletin = Regex.Match(name, @"(?<!\d)0*(?<n>\d{1,4})[-_ ]20\d{2}(?!\d)", RegexOptions.IgnoreCase).Groups["n"].Value;
        var bar = Regex.Match(name, @"BAR[-_ ]?0*(?<n>\d{1,4})", RegexOptions.IgnoreCase).Groups["n"].Value;
        if (!string.IsNullOrWhiteSpace(bulletin) && date.Success)
            bulletin = $"{ToInt(bulletin)}/{date.Groups["y"].Value}";
        if (string.IsNullOrWhiteSpace(bulletin)) bulletin = "—";
        if (string.IsNullOrWhiteSpace(bar)) bar = string.Empty;
        return (bulletin, bar, dateText);
    }

    public async Task<PaymentConferenceResult> RunAsync(
        IEnumerable<string> bulletinPaths,
        PaymentConferenceSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new PaymentConferenceResult { Month = settings.Month, Year = settings.Year, AcceptRubricPresence = settings.AcceptRubricPresence };
        if (settings.Month is < 1 or > 12 || settings.Year is < 2000 or > 2200)
            throw new ArgumentException("Informe uma competência válida para a conferência.");
        var requestedPaths = bulletinPaths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (requestedPaths.Count == 0) throw new InvalidOperationException("Selecione pelo menos um aditamento do furriel.");
        var paths = requestedPaths.Where(File.Exists).ToList();
        result.BulletinCount = requestedPaths.Count;
        result.Inventory.ExpectedBulletins = requestedPaths.Count;
        result.Inventory.MissingDocuments.AddRange(requestedPaths.Where(path => !File.Exists(path)).Select(path => Path.GetFileName(path) + ": arquivo não localizado."));

        var allMilitary = await LoadConferenceMilitaryAsync(cancellationToken);
        result.Inventory.ExpectedPaystubs = allMilitary.Count(x => Digits(x.Cpf).Length == 11 || Digits(x.PrecCp).Length >= 6);
        result.InputSignature = await BuildConferenceSignatureAsync(requestedPaths, settings, cancellationToken);
        var militaryByCpf = allMilitary.Where(x => Digits(x.Cpf).Length == 11)
            .GroupBy(x => Digits(x.Cpf), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var militaryByPrec = allMilitary.Where(x => Digits(x.PrecCp).Length >= 6)
            .GroupBy(x => Digits(x.PrecCp).TrimStart('0'), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var expected = new List<PaymentConferenceExpectedItem>();
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[index];
            progress?.Report($"Lendo aditamento {index + 1}/{paths.Count}: {Path.GetFileName(path)}");
            try
            {
                var pages = await _pdfText.ExtractPagesAsync(path, cancellationToken);
                result.Inventory.ExpectedPages += pages.Count;
                result.Inventory.ProcessedPages += pages.Count(page => !string.IsNullOrWhiteSpace(page));
                if (pages.Count == 0 || pages.All(string.IsNullOrWhiteSpace))
                    result.Inventory.UnreadableDocuments.Add(Path.GetFileName(path) + ": nenhuma página com texto legível.");
                var parsed = ParseExpectedItems(path, pages, settings, cancellationToken);
                expected.AddRange(parsed);
                result.AmbiguousBlocks.AddRange(DetectAmbiguousBlocks(path, pages, parsed));
                result.Inventory.ProcessedBulletins++;
                if (parsed.Count == 0) result.Warnings.Add($"{Path.GetFileName(path)}: nenhum item extraído. Verifique a leitura e os filtros; isso não significa ausência de pagamentos publicados.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Warnings.Add($"{Path.GetFileName(path)}: {ex.Message}");
                result.Inventory.UnreadableDocuments.Add(Path.GetFileName(path) + ": " + ex.Message);
                await _log.WriteAsync("Falha ao ler aditamento para conferência de pagamento.", ex);
            }
        }

        expected = expected.OrderBy(x => ParseDate(x.BulletinDate)).ThenBy(x => ToInt(x.Bulletin.Split('/')[0]))
            .ThenBy(x => x.Page).ToList();
        foreach (var item in expected)
        {
            MatchMilitary(item, militaryByCpf, militaryByPrec, allMilitary);
        }
        result.ExpectedItems = expected;

        var documents = await ReadConferencePdfsAsync(settings, result, progress, cancellationToken);
        for (var index = 0; index < expected.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = expected[index];
            progress?.Report($"Conferindo {index + 1}/{expected.Count}: {item.Name}");
            result.Rows.Add(CheckEvidence(item, ResolveMilitary(item, allMilitary), settings, documents));
        }
        FlagSharedEvidence(result);
        result.RubricHits = documents.Where(d => result.Rows.Any(r => r.PaystubPath == d.Path))
            .SelectMany(d => d.Rubrics.Select(r => new PaymentConferenceRubricHit
            {
                Military = result.Rows.First(x => x.PaystubPath == d.Path).Military,
                Cpf = d.Cpfs.FirstOrDefault() ?? "", PaystubPath = d.Path,
                Code = r.Code, Description = r.Description, Value = r.Value, Line = r.Line
            })).ToList();
        var previousDate = new DateTime(settings.Year, settings.Month, 1).AddMonths(-1);
        var previousSettings = JsonSerializer.Deserialize<PaymentConferenceSettings>(JsonSerializer.Serialize(settings))!;
        previousSettings.Month = previousDate.Month; previousSettings.Year = previousDate.Year;
        var previousInventory = new PaymentConferenceResult();
        var previousDocuments = await ReadConferencePdfsAsync(previousSettings, previousInventory, null, cancellationToken);
        var currentHits = ToRubricHits(documents, allMilitary);
        var previousHits = ToRubricHits(previousDocuments, allMilitary);
        var currentCpfs = documents.SelectMany(document => document.Cpfs).Select(Digits).Where(cpf => cpf.Length == 11)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var previousCpfs = previousDocuments.SelectMany(document => document.Cpfs).Select(Digits).Where(cpf => cpf.Length == 11)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        result.Inventory.ExpectedPreviousPaystubs = currentCpfs.Count;
        result.Inventory.FoundPreviousPaystubs = currentCpfs.Count(previousCpfs.Contains);
        if (result.Inventory.FoundPreviousPaystubs < result.Inventory.ExpectedPreviousPaystubs)
            result.Warnings.Add($"Linha de base da competência anterior disponível para {result.Inventory.FoundPreviousPaystubs}/{result.Inventory.ExpectedPreviousPaystubs} militar(es). A auditoria inversa permanece parcial para os demais; rubricas recorrentes não foram tratadas como novas sem base comparativa.");
        result.RubricChanges = PaymentMonthlyAuditRules.CompareRubricChanges(
            currentHits.Where(hit => previousCpfs.Contains(Digits(hit.Cpf))).ToList(),
            previousHits.Where(hit => currentCpfs.Contains(Digits(hit.Cpf))).ToList(), expected);
        if (result.InputSignature != await BuildConferenceSignatureAsync(requestedPaths, settings, cancellationToken))
        {
            result.Warnings.Add("Arquivos ou cadastros foram alterados durante a conferência. Refaça a leitura; este resultado não poderá ser salvo como cache válido.");
            foreach (var row in result.Rows.Where(r => r.Severity == "success"))
            {
                row.Status = "REVISÃO NECESSÁRIA"; row.Severity = "warning";
                row.Notes = "Os arquivos ou cadastros mudaram durante a execução. Refaça a conferência para confirmar este resultado.";
            }
        }
        result.Summary = BuildSummary(result.Rows, expected.Count);
        await ApplyVerificationsAsync(result);
        return result;
    }

    private static List<PaymentConferenceRubricHit> ToRubricHits(IEnumerable<ConferencePdf> documents, IReadOnlyList<MilitaryRecord> military)
        => documents.SelectMany(document => document.Rubrics.Select(rubric =>
        {
            var cpf = document.Cpfs.Count == 1 ? document.Cpfs[0] : string.Empty;
            var person = military.FirstOrDefault(item => Digits(item.Cpf) == cpf);
            return new PaymentConferenceRubricHit
            {
                Military = person?.Name ?? string.Empty, Cpf = cpf, PaystubPath = document.Path,
                Code = rubric.Code, Description = rubric.Description, Value = rubric.Value, Line = rubric.Line
            };
        })).ToList();

    public static IReadOnlyList<PaymentAmbiguousBlock> DetectAmbiguousBlocks(string path, IReadOnlyList<string> pages,
        IReadOnlyList<PaymentConferenceExpectedItem> parsed)
    {
        var metadata = ExtractBulletinMetadata(pages, path);
        var result = new List<PaymentAmbiguousBlock>();
        var relevant = new Regex(@"\b(FURRIEL|PAGAMENTO|SAQUE|ATRASAD[OA]S?|AUXILIO|FERIAS|GRATIFICACAO|ADICIONAL|IMPLANTACAO|EXCLUSAO|ALTERACAO|PENSAO|DESCONTO|RESTITUICAO|RUBRICA)\b", RegexOptions.IgnoreCase);
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var text = pages[pageIndex];
            if (string.IsNullOrWhiteSpace(text) || !relevant.IsMatch(Normalize(text))) continue;
            var pageItems = parsed.Where(item => item.Page == pageIndex + 1).ToList();
            var uncertain = pageItems.Count == 0 || pageItems.Any(item => item.PaymentType == "Outros Pagamentos"
                || item.ReviewReason.Length > 0 || item.ExpectedCodes.Count == 0 || string.IsNullOrWhiteSpace(item.Name));
            if (!uncertain) continue;
            var reason = pageItems.Count == 0 ? "Página financeiramente relevante sem item estruturado."
                : "Página com item genérico, incompleto ou de baixa confiança.";
            foreach (var block in SplitWithoutTruncation(text, 18_000))
                result.Add(new PaymentAmbiguousBlock
                {
                    SourceFile = path, BulletinNumber = metadata.Bulletin, BulletinDate = metadata.Date,
                    Page = pageIndex + 1, Text = block, Reason = reason
                });
        }
        return result;
    }

    private static IEnumerable<string> SplitWithoutTruncation(string text, int maxCharacters)
    {
        var current = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > maxCharacters)
                throw new InvalidOperationException("Uma linha extraída do PDF excede o limite seguro; o documento requer revisão local e não será truncado para IA.");
            if (current.Length > 0 && current.Length + line.Length + 1 > maxCharacters)
            {
                yield return current.ToString(); current.Clear();
            }
            current.AppendLine(line);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private async Task<List<MilitaryRecord>> LoadConferenceMilitaryAsync(CancellationToken cancellationToken)
    {
        var active = await _repository.GetAllAsync(cancellationToken);
        var result = active.ToList();
        try
        {
            var transferred = await _licensedTransferred.GetAllAsync(includeHidden: true, cancellationToken: cancellationToken);
            foreach (var item in transferred)
            {
                var record = item.ToMilitaryRecord();
                record.Id = -Math.Abs(item.Id);
                result.Add(record);
            }
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao carregar licenciados/transferidos para a Conferência de Pagamento.", ex);
        }

        return result
            .GroupBy(x =>
            {
                var cpf = Digits(x.Cpf);
                return cpf.Length == 11 ? $"CPF:{cpf}" : $"ID:{x.Id}";
            }, StringComparer.Ordinal)
            .Select(x => x.OrderByDescending(m => m.Id > 0).First())
            .ToList();
    }

    public async Task<string> ExportCsvAsync(PaymentConferenceResult result, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ReportsDirectory);
        var path = LastCsvFile;
        var sb = new StringBuilder();
        sb.AppendLine("Verificacao;Status;Competencia da folha;Militar;CPF;PREC;Boletim;Data;Pagina;Tipo;Modo;Codigos esperados;Valor publicado;Valor no PDF;Diferenca;Referencia do direito;Rubricas localizadas;Outras rubricas;Contracheque;Identificacao;Fonte da regra;Observacao");
        foreach (var row in result.Rows)
        {
            sb.AppendLine(string.Join(';', new[]
            {
                Csv(row.VerificationText), Csv(row.Status), Csv(row.ConferencePeriod), Csv(row.Military), Csv(MilitaryFormatting.FormatCpf(row.Cpf)), Csv(row.PrecCp), Csv(row.Bulletin), Csv(row.BulletinDate), Csv(row.BulletinPage.ToString(CultureInfo.InvariantCulture)),
                Csv(row.PaymentType), Csv(row.PaymentMode), Csv(row.ExpectedCodesText), Csv(row.ExpectedAmountText), Csv(row.PaidAmountText),
                Csv(row.DifferenceText), Csv(row.ReferencePeriod), Csv(row.RubricsFound), Csv(row.OtherRubrics), Csv(row.PaystubPath),
                Csv(row.IdentityEvidence), Csv(row.RuleSource), Csv(row.Notes)
            }));
        }
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, cancellationToken);
        return path;
    }

    public void OpenBulletinAtItem(PaymentConferenceExpectedItem item)
        => OpenPdfAndSearch(item.BulletinPath, BestSearchTerm(item), item.DocumentOccurrence);

    public void OpenBulletinAtRow(PaymentConferenceResultRow row)
        => OpenPdfAndSearch(row.BulletinPath, string.IsNullOrWhiteSpace(row.Cpf) ? row.Military : MilitaryFormatting.FormatCpf(row.Cpf), row.DocumentOccurrence);

    public void OpenPaystubAtRow(PaymentConferenceResultRow row)
    {
        var firstCode = Regex.Match(row.RubricsFound ?? string.Empty, @"\b[A-Z]{1,3}\d{3,5}\b").Value;
        var term = !string.IsNullOrWhiteSpace(firstCode)
            ? firstCode
            : KeywordsFor(row.PaymentType).FirstOrDefault() ?? row.PaymentType;
        OpenPdfAndSearch(row.PaystubPath, term, 1);
    }

    private static List<PaymentConferenceExpectedItem> ParseExpectedItems(string pdfPath,
        IReadOnlyList<string> pages, PaymentConferenceSettings? settings, CancellationToken cancellationToken)
        => ParsePublications(pdfPath, pages, settings, cancellationToken);

    private static bool IsContinuationNoise(string normalized)
        => normalized.StartsWith("(CONTINUACAO", StringComparison.Ordinal)
           || normalized.Contains("PAG N", StringComparison.Ordinal);

    private static int MonthNumber(string value)
    {
        var key = Normalize(value);
        return key switch
        {
            "JANEIRO" or "JAN" => 1,
            "FEVEREIRO" or "FEV" => 2,
            "MARCO" or "MAR" => 3,
            "ABRIL" or "ABR" => 4,
            "MAIO" or "MAI" => 5,
            "JUNHO" or "JUN" => 6,
            "JULHO" or "JUL" => 7,
            "AGOSTO" or "AGO" => 8,
            "SETEMBRO" or "SET" => 9,
            "OUTUBRO" or "OUT" => 10,
            "NOVEMBRO" or "NOV" => 11,
            "DEZEMBRO" or "DEZ" => 12,
            _ => 0
        };
    }

    private static bool ShouldInclude(string type, PaymentConferenceSettings? settings)
    {
        if (settings is null) return true;
        return type switch
        {
            "Férias" or "Indenização de férias" or "Adicional de férias — ajuste de contas" => settings.IncludeVacation,
            "Auxílio-Transporte" => settings.IncludeTransportAid,
            "Gratificação de Representação" => settings.IncludeGratification,
            "Adicional Habilitação" => settings.IncludeQualification,
            _ => settings.IncludeOthers
        };
    }

    private static List<PaystubRubric> ReadRubrics(string text)
    {
        var result = new List<PaystubRubric>();
        var cleaned = Regex.Replace(text ?? "", @"(?im)^.*(?:ACESSADO POR|ACESSO EM|EMITIDO POR).*$", "");
        cleaned = Regex.Replace(cleaned, @"\b([NAFDE][RD])\s+(\d{4})\b", "$1$2", RegexOptions.IgnoreCase);
        var codes = RubricCodeRegex().Matches(cleaned.ToUpperInvariant()).ToList();
        for (var i = 0; i < codes.Count; i++)
        {
            var code = codes[i].Value;
            var end = i + 1 < codes.Count ? codes[i + 1].Index : cleaned.Length;
            var block = cleaned[codes[i].Index..end];
            var footer = Regex.Match(block, @"(?im)\b(?:TOTAL(?:\s+DE)?\s+(?:RECEITAS?|DESCONTOS?|BRUTO|LIQUIDO)|LIQUIDO|LÍQUIDO|RECEITA\s+DESPESA|DATA IMP)\b");
            if (footer.Success) block = block[..footer.Index];
            var values = MoneyRegex().Matches(block).Select(m => ParseMoney(m.Value)).ToList();
            // In the SIPPES table "VALOR % R/D IR PARC", the second decimal is a percentage.
            if (values.Count > 1 && Regex.IsMatch(Normalize(text), @"VALOR\s+%\s+R\s*/\s*D")) values = [values[0]];
            // Preserve zero and unreadable values: rubric presence is independent of amount extraction.
            result.Add(new PaystubRubric(code, DescriptionFromBlock(code, block), OneLine(block), Normalize(block),
                values.Count == 1 ? values[0] : 0, values));
        }
        return result;
    }

    private static List<PaystubRubric> FindRubricMatches(PaymentConferenceExpectedItem item, List<PaystubRubric> rubrics, bool requirePrefix)
    {
        if (item.ExpectedCodes.Count == 0) return [];
        return rubrics.Where(r => item.ExpectedCodes.Contains(r.Code, StringComparer.OrdinalIgnoreCase)
            || (!requirePrefix && item.ExpectedCodes.Any(c => c.Length == r.Code.Length && c[2..] == r.Code[2..]))).ToList();
    }

    private static IReadOnlyList<string> KeywordsFor(string type)
    {
        return type switch
        {
            "Auxílio-Transporte" => ["AUXILIO TRANSPORTE", "AUX TRANSP", "TRANSPORTE"],
            "Férias" => ["FERIAS", "ADICIONAL DE FERIAS", "1/3 FERIAS", "FERIAS INDENIZADAS"],
            "Gratificação de Representação" => ["GRATIFICACAO", "REPRESENTACAO", "GRAT REP"],
            "Adicional Habilitação" => ["HABILITACAO", "ADICIONAL DE HABILITACAO", "ADIC HABILIT"],
            "Auxílio-Alimentação" => ["AUXILIO ALIMENTACAO", "AUX ALIMENT", "ALIMENTACAO"],
            "Exercícios Anteriores" => ["EXERCICIO ANTERIOR"],
            _ => []
        };
    }

    private void MatchMilitary(
        PaymentConferenceExpectedItem item,
        Dictionary<string, MilitaryRecord> byCpf,
        Dictionary<string, MilitaryRecord> byPrec,
        IReadOnlyList<MilitaryRecord> allMilitary)
    {
        MilitaryRecord? match = null;
        var cpf = Digits(item.Cpf);
        var prec = Digits(item.PrecCp).TrimStart('0');
        var byCpfRecord = cpf.Length == 11 ? allMilitary.Where(m => Digits(m.Cpf) == cpf).ToList() : [];
        var byPrecRecord = prec.Length >= 6 ? allMilitary.Where(m => Digits(m.PrecCp).TrimStart('0') == prec).ToList() : [];
        if (byCpfRecord.Count == 1) match = byCpfRecord[0];
        else if (byCpfRecord.Count == 0 && byPrecRecord.Count == 1 &&
                 (cpf.Length != 11 || Digits(byPrecRecord[0].Cpf).Length != 11 || Digits(byPrecRecord[0].Cpf) == cpf)) match = byPrecRecord[0];
        if (match is not null && prec.Length >= 6 && Digits(match.PrecCp).Length >= 6 && Digits(match.PrecCp).TrimStart('0') != prec)
        {
            item.MatchStatus = "CPF/PREC divergentes no cadastro";
            return;
        }
        if (match is null && cpf.Length != 11 && prec.Length < 6)
        {
            var sameNames = allMilitary.Where(m => CompactIdentity(m.Name) == CompactIdentity(item.Name)).ToList();
            if (sameNames.Count == 1) match = sameNames[0];
            else if (sameNames.Count > 1) { item.MatchStatus = "Nome ambíguo no cadastro"; return; }
        }
        if (match is null)
        {
            item.MatchStatus = "Não localizado no banco";
            return;
        }
        item.MatchedMilitaryId = match.Id;
        item.MatchedMilitaryName = $"{MilitaryRankService.ShortName(match.Rank)} {match.Name}".Trim();
        item.MatchStatus = match.Id < 0 ? "Localizado em LT" : "Localizado no banco";
    }

    private static MilitaryRecord? ResolveMilitary(PaymentConferenceExpectedItem item, IReadOnlyList<MilitaryRecord> allMilitary)
    {
        if (item.MatchedMilitaryId is int id)
        {
            var byId = allMilitary.FirstOrDefault(x => x.Id == id);
            if (byId is not null) return byId;
        }
        return null;
    }

    private static PaymentConferenceSummary BuildSummary(IEnumerable<PaymentConferenceResultRow> rows, int expectedCount)
    {
        var list = rows.ToList();
        return new PaymentConferenceSummary
        {
            Expected = expectedCount,
            Ok = list.Count(x => x.Status == "ACHOU RUBRICA"),
            MissingPaystub = list.Count(x => x.Status == "SEM CONTRACHEQUE"),
            MissingRubric = list.Count(x => x.Status is "NÃO ACHOU RUBRICA" or "NÃO RECEBEU"),
            Divergent = list.Count(x => x.Status is "VALOR DIVERGENTE" or "NATUREZA DIFERENTE"),
            Attention = list.Count(x => x.Status is "REVISÃO NECESSÁRIA" or "ERRO AO LER"),
            NotApplicable = list.Count(x => x.Status is "ATO CADASTRAL" or "OUTRA COMPETÊNCIA")
        };
    }

    private static (string Bulletin, string Bar, string Date) ExtractBulletinMetadata(IReadOnlyList<string> pages, string path)
    {
        var head = string.Join("\n", pages.Take(2));
        var bulletin = Regex.Match(head, @"ADITAMENTO\s+DO\s+FURRIEL\s+N[º°O]?\s*(\d{1,4}/\d{4}|\d{1,4})", RegexOptions.IgnoreCase).Groups[1].Value;
        var bar = Regex.Match(head, @"\bBAR\s*(\d{1,4})\b", RegexOptions.IgnoreCase).Groups[1].Value;
        var date = Regex.Match(head, @"(\d{1,2}\s+de\s+[A-Za-zÀ-ÿ]+\s+de\s+20\d{2})", RegexOptions.IgnoreCase).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(date)) date = Regex.Match(head, @"\b\d{1,2}/\d{1,2}/20\d{2}\b").Value;
        if (string.IsNullOrWhiteSpace(bulletin)) bulletin = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(?<!\d)(\d{1,4})(?!\d)").Groups[1].Value;
        return (string.IsNullOrWhiteSpace(bulletin) ? "—" : bulletin, bar, string.IsNullOrWhiteSpace(date) ? "—" : ToBrDate(date));
    }

    private static string DescriptionFromBlock(string code, string block)
    {
        var text = Regex.Replace(block ?? string.Empty, "^" + Regex.Escape(code) + @"\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        var amount = MoneyRegex().Match(text);
        if (amount.Success) text = text[..amount.Index].Trim();
        return text.Length == 0 ? "(descrição não extraída)" : OneLine(text);
    }

    private static string BestSearchTerm(PaymentConferenceExpectedItem item)
        => !string.IsNullOrWhiteSpace(item.Cpf) ? MilitaryFormatting.FormatCpf(item.Cpf) : item.Name;

    private static void OpenPdfAndSearch(string path, string searchTerm, int occurrence)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        ShellService.OpenPath(path);
        var term = (searchTerm ?? string.Empty).Trim();
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(term)) return;

        CancellationTokenSource cancellation;
        lock (PdfSearchSync)
        {
            _pdfSearchCancellation?.Cancel();
            _pdfSearchCancellation?.Dispose();
            _pdfSearchCancellation = cancellation = new CancellationTokenSource();
        }
        var cancellationToken = cancellation.Token;

        try { Clipboard.SetText(term); } catch { }
        _ = Task.Run(async () =>
        {
            try
            {
                // O visualizador pode demorar para assumir o foco. Em aberturas rápidas,
                // somente a pesquisa mais recente continua; as anteriores são canceladas.
                await Task.Delay(800, cancellationToken);
                var pdfWindow = await WaitForExternalForegroundWindowAsync(cancellationToken);
                if (pdfWindow == IntPtr.Zero || !IsForegroundWindow(pdfWindow)) return;

                SendCtrlFAndPaste();
                await Task.Delay(550, cancellationToken);

                // Nunca envie Esc/F3 se o foco voltou ao SIGFUR ou mudou de janela.
                // Era esse Esc global atrasado que podia fechar a Conferência de Pagamento.
                if (!IsForegroundWindow(pdfWindow)) return;
                PressKey(VkEscape);
                await Task.Delay(120, cancellationToken);
                for (var index = 1; index < Math.Max(1, occurrence); index++)
                {
                    if (!IsForegroundWindow(pdfWindow)) return;
                    PressKey(VkF3);
                    await Task.Delay(110, cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private static async Task<IntPtr> WaitForExternalForegroundWindowAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = GetForegroundWindow();
            if (window != IntPtr.Zero)
            {
                _ = GetWindowThreadProcessId(window, out var processId);
                if (processId != (uint)Environment.ProcessId) return window;
            }
            await Task.Delay(100, cancellationToken);
        }
        return IntPtr.Zero;
    }

    private static bool IsForegroundWindow(IntPtr expectedWindow)
    {
        if (expectedWindow == IntPtr.Zero || GetForegroundWindow() != expectedWindow) return false;
        _ = GetWindowThreadProcessId(expectedWindow, out var processId);
        return processId != 0 && processId != (uint)Environment.ProcessId;
    }

    private static string Csv(string value)
    {
        value ??= string.Empty;
        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static string CleanContext(string value)
        => Regex.Replace(value ?? string.Empty, @"[ \t]+", " ").Trim();

    private static string CleanRank(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static string CleanName(string value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim(' ', '-', '.', ':');
    private static string OneLine(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    private static string Digits(string? value) => Regex.Replace(value ?? string.Empty, @"\D+", string.Empty);
    private static int ToInt(string? value) => int.TryParse(Regex.Match(value ?? string.Empty, @"\d+").Value, out var number) ? number : 0;
    private static long SafeFileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static bool LooksLikePaystubPdfFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Contains(".pdf.", StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
    }

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string CachePath(PaymentConferenceSettings settings, IReadOnlyList<string> bulletinPaths)
        => Path.Combine(_paths.PaymentConferenceCacheDirectory, $"conference_{settings.Year}_{settings.Month:00}_{HashText(SettingsSignature(settings) + string.Join("|", bulletinPaths))[..12]}.json");

    private async Task<string> BuildConferenceSignatureAsync(IReadOnlyList<string> bulletinPaths, PaymentConferenceSettings settings, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(ParserVersion).AppendLine(SettingsSignature(settings));
        foreach (var path in bulletinPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            AppendFileSignature(sb, path);

        AppendFileSignature(sb, _paths.DatabaseFile);
        AppendFileSignature(sb, _paths.DatabaseFile + "-wal");
        var folder = string.IsNullOrWhiteSpace(settings.PaystubFolder) ? _paths.PaystubsDirectory : settings.PaystubFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            var root = Path.GetFullPath(folder);
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.pdf*", SearchOption.AllDirectories)
                             .Where(LooksLikePaystubPdfFile)
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = new FileInfo(file);
                    sb.Append("paystub|")
                        .Append(Path.GetRelativePath(root, file)).Append('|')
                        .Append(info.Length).Append('|')
                        .Append(info.LastWriteTimeUtc.Ticks)
                        .AppendLine();
                }
            }, cancellationToken);
        }

        return HashText(sb.ToString());
    }

    private static void AppendFileSignature(StringBuilder sb, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            sb.Append("missing|").Append(path).AppendLine();
            return;
        }
        var info = new FileInfo(path);
        sb.Append("bulletin|")
            .Append(Path.GetFullPath(path)).Append('|')
            .Append(info.Length).Append('|')
            .Append(info.LastWriteTimeUtc.Ticks)
            .AppendLine();
    }

    private static string SettingsSignature(PaymentConferenceSettings settings)
        => JsonSerializer.Serialize(new
        {
            settings.Month,
            settings.Year,
            PaystubFolder = string.IsNullOrWhiteSpace(settings.PaystubFolder) ? string.Empty : Path.GetFullPath(settings.PaystubFolder),
            settings.Tolerance,
            settings.RequirePrefix,
            settings.AcceptRubricPresence,
            settings.IncludeVacation,
            settings.IncludeTransportAid,
            settings.IncludeGratification,
            settings.IncludeQualification,
            settings.IncludeOthers
        });

    private static List<string> NormalizePaths(IEnumerable<string> paths)
        => paths.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool SamePathSet(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count && left.Zip(right).All(x => x.First.Equals(x.Second, StringComparison.OrdinalIgnoreCase));

    private static bool PathEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ParseDate(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        foreach (var fmt in new[] { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "yyyy-MM-dd" })
            if (DateTime.TryParseExact(text, fmt, PtBr, DateTimeStyles.None, out var dt)) return dt;
        if (DateTime.TryParse(text, PtBr, DateTimeStyles.None, out var parsed)) return parsed;
        return DateTime.MinValue;
    }

    private static string ToBrDate(string? value)
    {
        var parsed = ParseDate(value);
        return parsed == DateTime.MinValue ? (value ?? "—") : parsed.ToString("dd/MM/yyyy", PtBr);
    }

    private static double ParseMoney(string? value)
    {
        var text = (value ?? string.Empty).Replace("R$", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (text.Length == 0) return 0;
        if (text.Contains(',')) text = text.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? Math.Abs(number) : 0;
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToUpperInvariant(c));
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    [GeneratedRegex(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b", RegexOptions.Compiled)]
    private static partial Regex CpfRegex();

    [GeneratedRegex(@"Prec\s*[-/]?\s*CP\s*[:\-]?\s*([\d.\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PrecRegex();

    [GeneratedRegex(@"(?<!\d)(?:R\$\s*)?[-+]?\d{1,3}(?:\.\d{3})*,\d{2}|(?<!\d)(?:R\$\s*)?[-+]?\d+,\d{2}(?!\d)", RegexOptions.Compiled)]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"\b([A-Z]{1,3}\d{3,5})\b", RegexOptions.Compiled)]
    private static partial Regex RubricCodeRegex();

    public sealed record PaymentConferenceCacheLoad(DateTime SavedAt, PaymentConferenceResult Result, string Path);

    private sealed class PaymentConferenceCacheStore
    {
        public string Kind { get; set; } = "payment-conference";
        public string ParserVersion { get; set; } = string.Empty;
        public DateTime SavedAt { get; set; }
        public PaymentConferenceSettings Settings { get; set; } = new();
        public List<string> BulletinPaths { get; set; } = [];
        public string Signature { get; set; } = string.Empty;
        public PaymentConferenceResult Result { get; set; } = new();
    }
    private sealed record PaystubRubric(string Code, string Description, string Line, string Normalized, double Value, IReadOnlyList<double> MoneyValues)
    {
        public string CompactText => $"{Code} - {Description}" + (MoneyValues.Count == 1 ? $" · {MilitaryFormatting.FormatMoney(Value)}" : " · valor a revisar");
    }

    // Win32: abrir o PDF e deixar a pesquisa pronta no termo do militar/rubrica.
    private const ushort VkControl = 0x11;
    private const ushort VkF = 0x46;
    private const ushort VkV = 0x56;
    private const ushort VkEscape = 0x1B;
    private const ushort VkF3 = 0x72;
    private const uint KeyeventfKeyup = 0x0002;

    private static void SendCtrlFAndPaste()
    {
        Key(VkControl, false); Key(VkF, false); Key(VkF, true); Key(VkControl, true);
        Thread.Sleep(120);
        Key(VkControl, false); Key(VkV, false); Key(VkV, true); Key(VkControl, true);
    }

    private static void PressKey(ushort key) { Key(key, false); Thread.Sleep(35); Key(key, true); }
    private static void Key(ushort key, bool up) => keybd_event((byte)key, 0, up ? KeyeventfKeyup : 0, UIntPtr.Zero);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
