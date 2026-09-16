using System.IO.Compression;
using System.Text;

namespace SIGFUR.Wpf.Services;

public sealed class BackupService
{
    private readonly AppPaths _paths;

    public BackupService(AppPaths paths) => _paths = paths;

    public async Task<string> CreateAsync(string prefix = "backup", int maxBackups = 5, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.BackupsDirectory);
        var final = Path.Combine(_paths.BackupsDirectory, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        var temp = Path.Combine(_paths.BackupsDirectory, $"sigfur_tmp_{Guid.NewGuid():N}.zip");

        try
        {
            await Task.Run(() =>
            {
                var skipped = new List<string>();
                using var archive = ZipFile.Open(temp, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(_paths.DataDirectory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(_paths.DataDirectory, file);
                    if (relative.StartsWith("backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    if (relative.StartsWith("Cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

                    try { AddFileShared(archive, file, Path.Combine("DATA_DIR", relative)); }
                    catch (Exception ex) { skipped.Add($"{relative}: {ex.Message}"); }
                }

                var manifest = new StringBuilder()
                    .AppendLine("SIGFUR backup")
                    .AppendLine($"CreatedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                    .AppendLine($"DataDirectory={_paths.DataDirectory}")
                    .AppendLine($"Skipped={skipped.Count}");
                foreach (var item in skipped) manifest.AppendLine(item);
                var entry = archive.CreateEntry("MANIFEST.txt", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(manifest.ToString());
            }, cancellationToken);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }

        File.Move(temp, final, true);
        Cleanup(maxBackups);
        return final;
    }

    private static void AddFileShared(ZipArchive archive, string file, string entryName)
    {
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private void Cleanup(int maxBackups)
    {
        try
        {
            var backups = Directory.EnumerateFiles(_paths.BackupsDirectory, "*.zip")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            foreach (var old in backups.Skip(Math.Max(1, maxBackups)))
                try { File.Delete(old); } catch { }
        }
        catch { }
    }
}
