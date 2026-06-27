using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

/// <summary>
/// Captura nativa de ficha financeira. O portal é aberto no navegador padrão,
/// o CPF fica no clipboard e o serviço monitora a pasta Downloads até o PDF
/// terminar de baixar. Não usa Python, Poppler ou processos auxiliares.
/// </summary>
public sealed class FinancialStatementCaptureService
{
    public const string PortalUrl = "https://cpex-intranet.eb.mil.br/area_ua_cpex/fichas/index.html";

    private readonly AppPaths _paths;
    private readonly LogService _log;

    public FinancialStatementCaptureService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<string> CaptureAsync(
        MilitaryRecord military,
        int year,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cpf = MilitaryFormatting.Digits(military.Cpf);
        if (cpf.Length != 11) throw new InvalidOperationException("O militar não possui CPF válido para pesquisar a ficha financeira.");
        if (year is < 2000 or > 2200) throw new InvalidOperationException("Informe um ano válido para a ficha financeira.");

        var directories = CandidateDownloadDirectories().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (directories.Count == 0) throw new DirectoryNotFoundException("A pasta Downloads do usuário não foi localizada.");

        var startedAt = DateTime.Now.AddSeconds(-2);
        var existing = Snapshot(directories);
        progress?.Report("CPF copiado. Abrindo o portal da ficha financeira…");
        ShellService.OpenPath(PortalUrl);

        progress?.Report("Pesquise o militar no portal e baixe o PDF. O SIGFUR está aguardando o arquivo…");
        var timeoutAt = DateTime.UtcNow.AddMinutes(8);
        string? candidate = null;
        long previousSize = -1;
        var stableHits = 0;

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidate = FindNewestPdf(directories, existing, startedAt);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                try
                {
                    var info = new FileInfo(candidate);
                    if (info.Exists && info.Length > 2048)
                    {
                        if (info.Length == previousSize) stableHits++; else stableHits = 0;
                        previousSize = info.Length;
                        progress?.Report($"PDF detectado ({Math.Max(1, info.Length / 1024):N0} KB). Aguardando finalizar…");
                        if (stableHits >= 2 && IsPdf(candidate)) break;
                    }
                }
                catch { }
            }
            await Task.Delay(900, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate) || !IsPdf(candidate))
            throw new TimeoutException("Nenhum PDF novo foi detectado na pasta Downloads. Baixe a ficha no portal e tente novamente.");

        var root = PersonDocumentStorageService.ResolveConfiguredRoot(_paths);
        var settings = new CpexPaystubSettings { OutputDirectory = root };
        var person = new CpexPaystubPerson(military.Name, military.Cpf, military.ShortRank, military.Id, military.MilitaryId, military.PrecCp);
        var destination = CpexPaystubAutomationService.BuildFinancialStatementOutputPath(settings, person, year);
        if (!SamePath(candidate, destination)) File.Copy(candidate, destination, overwrite: true);
        progress?.Report("Ficha financeira salva na subpasta do militar.");
        await _log.WriteAsync($"Ficha financeira capturada: {destination}");
        return destination;
    }

    private static HashSet<string> Snapshot(IEnumerable<string> directories)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.pdf", SearchOption.TopDirectoryOnly))
                    result.Add(Path.GetFullPath(file));
            }
            catch { }
        }
        return result;
    }

    private static string? FindNewestPdf(IEnumerable<string> directories, HashSet<string> existing, DateTime startedAt)
    {
        var candidates = new List<FileInfo>();
        foreach (var directory in directories)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.pdf", SearchOption.TopDirectoryOnly))
                {
                    var full = Path.GetFullPath(file);
                    var info = new FileInfo(full);
                    if (existing.Contains(full) && info.LastWriteTime < startedAt) continue;
                    if (info.LastWriteTime >= startedAt) candidates.Add(info);
                }
            }
            catch { }
        }
        return candidates.OrderByDescending(x => x.LastWriteTime).ThenByDescending(x => x.Length).FirstOrDefault()?.FullName;
    }

    private static IEnumerable<string> CandidateDownloadDirectories()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, "Downloads");
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrWhiteSpace(oneDrive)) yield return Path.Combine(oneDrive, "Downloads");
        yield return Path.GetTempPath();
    }

    private static bool IsPdf(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[5];
            return stream.Read(header) == 5 && header.SequenceEqual("%PDF-"u8);
        }
        catch { return false; }
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static string SafeFileName(string value)
    {
        var clean = string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        foreach (var invalid in Path.GetInvalidFileNameChars()) clean = clean.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(clean) ? "MILITAR" : clean.ToUpperInvariant();
    }

    private static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 1000; index++)
        {
            path = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(directory, $"{stem} {DateTime.Now:yyyyMMdd_HHmmss}{extension}");
    }
}
