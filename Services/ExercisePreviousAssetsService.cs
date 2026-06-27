using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class ExercisePreviousAssetsService
{
    private readonly AppPaths _paths;
    public ExercisePreviousAssetsService(AppPaths paths) => _paths = paths;

    public string RootDirectory => _paths.ExercisePreviousDirectory;
    public string TemplatesDirectory => _paths.ExercisePreviousTemplatesDirectory;
    public string OutputDirectory => _paths.ExercisePreviousOutputDirectory;
    public string TemplateWorkbook => Path.Combine(TemplatesDirectory, "EA_IPCAE_Template.xlsm");
    public string CoverTemplate => Path.Combine(TemplatesDirectory, "CAPA_TEMPLATE.docx");
    public string RequestTemplate => Path.Combine(TemplatesDirectory, "REQUERIMENTO_TEMPLATE.docx");

    public void EnsureInstalled()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(TemplatesDirectory);
        Directory.CreateDirectory(OutputDirectory);
        Directory.CreateDirectory(_paths.ExercisePreviousLogsDirectory);
        Directory.CreateDirectory(_paths.ExercisePreviousProtocolsDirectory);
        Directory.CreateDirectory(_paths.ExercisePreviousCpexDownloadsDirectory);
        CopyShipped("EA_IPCAE_Template.xlsm", TemplateWorkbook);
        CopyShipped("CAPA_TEMPLATE.docx", CoverTemplate);
        CopyShipped("REQUERIMENTO_TEMPLATE.docx", RequestTemplate);
    }

    private static void CopyShipped(string name, string destination)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "EA", name),
            Path.Combine(AppContext.BaseDirectory, name),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", "EA", name))
        };
        var source = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Recurso obrigatório do EA não encontrado: {name}");
        if (!File.Exists(destination) || new FileInfo(destination).Length != new FileInfo(source).Length)
            File.Copy(source, destination, true);
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
