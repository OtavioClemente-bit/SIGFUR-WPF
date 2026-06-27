using System.IO.Compression;

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

        await Task.Run(() =>
        {
            using var archive = ZipFile.Open(temp, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(_paths.DataDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(_paths.DataDirectory, file);
                if (relative.StartsWith("backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                try { archive.CreateEntryFromFile(file, Path.Combine("DATA_DIR", relative), CompressionLevel.Optimal); }
                catch { }
            }
        }, cancellationToken);

        File.Move(temp, final, true);
        Cleanup(maxBackups);
        return final;
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
