using System.Collections.Specialized;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;

namespace SIGFUR.Wpf.Services;

public static class PaystubClipboardService
{
    public static int CopyPackage(IEnumerable<string> filePaths, string message, string? archiveName = null)
    {
        var files = ExistingFiles(filePaths);
        var clipboardFiles = PrepareFilesForSharing(files, archiveThreshold: 1, archiveName);

        var fileDropList = new StringCollection();
        fileDropList.AddRange(clipboardFiles.ToArray());

        var data = new DataObject();
        data.SetFileDropList(fileDropList);
        if (!string.IsNullOrWhiteSpace(message))
        {
            data.SetData(DataFormats.UnicodeText, message);
            data.SetData(DataFormats.Text, message);
        }

        SetClipboardData(data);
        return files.Length;
    }

    public static void CopyFilesOnly(IEnumerable<string> filePaths)
    {
        var files = ExistingFiles(filePaths);
        var fileDropList = new StringCollection();
        fileDropList.AddRange(files);
        var data = new DataObject();
        data.SetFileDropList(fileDropList);
        SetClipboardData(data);
    }

    public static void CopyTextOnly(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text ?? string.Empty);
        data.SetData(DataFormats.Text, text ?? string.Empty);
        SetClipboardData(data);
    }

    private static void SetClipboardData(DataObject data)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (ExternalException) when (attempt < 3)
            {
                Thread.Sleep(75 * (attempt + 1));
            }
        }
    }

    public static IReadOnlyList<string> PrepareFilesForSharing(
        IEnumerable<string> filePaths,
        int archiveThreshold,
        string? archiveName = null)
    {
        var files = ExistingFiles(filePaths);
        return files.Length > Math.Max(0, archiveThreshold)
            ? [CreateArchive(files, archiveName)]
            : files;
    }

    private static string[] ExistingFiles(IEnumerable<string> filePaths)
    {
        var files = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException("Nenhum contracheque foi encontrado para compartilhar.");
        return files;
    }

    private static string CreateArchive(IReadOnlyList<string> files, string? requestedName)
    {
        var folder = Path.GetDirectoryName(files[0])
                     ?? throw new InvalidOperationException("Não foi possível preparar a pasta temporária dos contracheques.");
        Directory.CreateDirectory(folder);

        var baseName = SafeArchiveName(requestedName);
        var archivePath = Path.Combine(folder, baseName);
        if (File.Exists(archivePath))
        {
            var stem = Path.GetFileNameWithoutExtension(baseName);
            archivePath = Path.Combine(folder, $"{stem} - {DateTime.Now:yyyyMMdd-HHmmssfff}.zip");
        }

        using var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var entryName = UniqueEntryName(Path.GetFileName(file), entryNames);
            // PDFs já possuem compressão interna. Tentar comprimi-los novamente
            // consome muito tempo e quase não reduz o tamanho do pacote.
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.NoCompression);
        }

        return archivePath;
    }

    private static string SafeArchiveName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Contracheques.zip" : Path.GetFileName(value.Trim());
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray())
            .Trim(' ', '.', '_');
        if (string.IsNullOrWhiteSpace(name)) name = "Contracheques.zip";
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
        return name;
    }

    private static string UniqueEntryName(string fileName, ISet<string> usedNames)
    {
        if (usedNames.Add(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (usedNames.Add(candidate)) return candidate;
        }
    }
}
