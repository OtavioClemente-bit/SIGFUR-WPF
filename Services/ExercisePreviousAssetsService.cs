using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class ExercisePreviousAssetsService
{
    private readonly AppPaths _paths;
    public ExercisePreviousAssetsService(AppPaths paths) => _paths = paths;

    public string RootDirectory => _paths.ExercisePreviousDirectory;
    public string TemplatesDirectory => _paths.ExercisePreviousTemplatesDirectory;
    public string OutputDirectory => _paths.ExercisePreviousOutputDirectory;
    public string TemplateWorkbook => Path.Combine(TemplatesDirectory, "EA_IPCAE_TEMPLATE.xlsm");
    public string CoverTemplate => Path.Combine(TemplatesDirectory, "EA_CAPA_TEMPLATE.docx");
    public string RequestTemplate => Path.Combine(TemplatesDirectory, "EA_REQUERIMENTO_TEMPLATE.docx");

    public void EnsureInstalled()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(TemplatesDirectory);
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(_paths.ExercisePreviousLogsDirectory);
        Directory.CreateDirectory(_paths.ExercisePreviousProtocolsDirectory);
        Directory.CreateDirectory(_paths.ExercisePreviousCpexDownloadsDirectory);
        CopyShipped("EA_IPCAE_TEMPLATE.xlsm", TemplateWorkbook);
        CopyShipped("EA_CAPA_TEMPLATE.docx", CoverTemplate);
        CopyShipped("EA_REQUERIMENTO_TEMPLATE.docx", RequestTemplate);
    }

    private static void CopyShipped(string name, string destination)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "templates", "docs", name),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "templates", "docs", name))
        };
        var source = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Recurso obrigatório do EA não encontrado: {name}");
        if (!File.Exists(destination) || !FilesEqual(source, destination))
            File.Copy(source, destination, true);
    }

    private static bool FilesEqual(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        var leftHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(left));
        var rightHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(right));
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    public string CreateWorkbookOutputPath(ExercisePreviousProcess process)
    {
        var name = SafeName(string.IsNullOrWhiteSpace(process.WarName) ? process.FullName : process.WarName);
        var folder = Path.Combine(OutputDirectory, $"Processo_{Math.Max(0, process.Id):0000}_{name}");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"EA_{Math.Max(0, process.Id):0000}_{name}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsm");
    }

    public string GetProcessFolder(ExercisePreviousProcess process)
    {
        var name = SafeName(string.IsNullOrWhiteSpace(process.WarName) ? process.FullName : process.WarName);
        var folder = Path.Combine(OutputDirectory, $"Processo_{Math.Max(0, process.Id):0000}_{name}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string SafeName(string? value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((value ?? "MILITAR").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "MILITAR" : clean;
    }
}
