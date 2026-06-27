using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class SigfurBackupService
{
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public async Task<SigfurBackupInfo> CreateBackupAsync(SigfurProfileSession session, CancellationToken ct = default)
    {
        if (!session.IsProfileMode || session.Profile is null || session.Config is null)
            throw new InvalidOperationException("Backup de sincronização exige um Perfil SIGFUR ativo.");

        var localPath = session.Config.LocalDataPath;
        var syncPath = session.Config.SyncFolderPath;
        if (!Directory.Exists(localPath)) throw new DirectoryNotFoundException("Pasta local dos dados não encontrada: " + localPath);
        Directory.CreateDirectory(syncPath);
        Directory.CreateDirectory(Path.Combine(syncPath, "Backups"));
        Directory.CreateDirectory(Path.Combine(syncPath, "Logs"));

        await WriteLogAsync(syncPath, $"Início do backup | perfil={session.Profile.ProfileName} | local={localPath}");

        var temp = Path.Combine(syncPath, "backup_temp_" + Guid.NewGuid().ToString("N") + ".sigfurbak");
        var latest = Path.Combine(syncPath, "ultimo.sigfurbak");
        var versioned = Path.Combine(syncPath, "Backups", $"SIGFUR_BACKUP_{DateTime.Now:yyyy-MM-dd_HHmm}.sigfurbak");
        var metadata = new SigfurBackupMetadata
        {
            ProfileName = session.Profile.ProfileName,
            CreatedAt = DateTime.Now,
            SourcePath = localPath,
            MachineName = Environment.MachineName
        };

        try
        {
            var files = EnumerateBackupFiles(localPath).ToList();
            metadata.TotalFiles = files.Count;
            metadata.TotalBytes = files.Sum(x => new FileInfo(x).Length);
            metadata.Checksum = ComputeFilesChecksum(files, localPath);

            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                archive.CreateEntry("data/");
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(localPath, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, "data/" + relative, CompressionLevel.Optimal);
                }

                var entry = archive.CreateEntry("metadata.json", CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await JsonSerializer.SerializeAsync(stream, metadata, _jsonOptions, ct);
            }

            ValidateBackup(temp);
            File.Copy(temp, versioned, true);
            File.Move(temp, latest, true);

            session.Profile.LastUpdatedAt = DateTime.Now;
            session.Profile.LastBackupFile = "ultimo.sigfurbak";
            await new JsonFileService().SaveAsync(ProfileService.ProfileMetadataPath(syncPath), session.Profile);

            var info = await GetLastBackupInfoAsync(syncPath);
            await WriteLogAsync(syncPath, $"Fim do backup | arquivo={Path.GetFileName(latest)} | arquivos={metadata.TotalFiles} | bytes={metadata.TotalBytes}");
            return info ?? new SigfurBackupInfo { FilePath = latest, Metadata = metadata, LastWriteTime = File.GetLastWriteTime(latest), Length = new FileInfo(latest).Length };
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            await WriteLogAsync(syncPath, "Erro no backup | " + ex.Message);
            throw;
        }
    }

    public async Task<SigfurBackupInfo?> GetLastBackupInfoAsync(string syncFolder)
    {
        var path = Path.Combine(syncFolder, "ultimo.sigfurbak");
        if (!File.Exists(path)) return null;
        var metadata = await ReadMetadataAsync(path);
        var file = new FileInfo(path);
        return new SigfurBackupInfo { FilePath = path, Metadata = metadata, LastWriteTime = file.LastWriteTime, Length = file.Length };
    }

    public static void ValidateBackup(string backupPath)
    {
        if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
            throw new InvalidDataException("Backup não foi criado corretamente.");

        try
        {
            using var archive = ZipFile.OpenRead(backupPath);
            if (archive.Entries.Count == 0)
                throw new InvalidDataException("Backup inválido: arquivo ZIP vazio.");

            var hasMetadata = archive.GetEntry("metadata.json") is not null
                              || archive.Entries.Any(x => string.Equals(Path.GetFileName(x.FullName), "metadata.json", StringComparison.OrdinalIgnoreCase));
            if (!hasMetadata && !ArchiveContainsRecognizedSigfurData(archive))
                throw new InvalidDataException("Backup encontrado, mas não contém metadata.json nem arquivos reconhecidos do SIGFUR.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Backup inválido: não foi possível abrir o arquivo de backup.", ex);
        }
    }

    public async Task<SigfurBackupMetadata?> ReadMetadataAsync(string backupPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(backupPath);
            var entry = archive.GetEntry("metadata.json")
                        ?? archive.Entries.FirstOrDefault(x => string.Equals(Path.GetFileName(x.FullName), "metadata.json", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            await using var stream = entry.Open();
            return await JsonSerializer.DeserializeAsync<SigfurBackupMetadata>(stream);
        }
        catch { return null; }
    }

    private static bool ArchiveContainsRecognizedSigfurData(ZipArchive archive)
    {
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "militares.db",
            "config.json",
            "app_settings.json",
            "wpf_ui_state.json",
            "database_location_wpf.json"
        };
        var knownDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "boletim_furriel",
            "legislacao",
            "documentos_militares",
            "Contracheques",
            "contracheques"
        };

        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(name) && knownFiles.Contains(name))
                return true;

            var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => knownDirectories.Contains(part)))
                return true;
        }

        return false;
    }

    public static async Task WriteLogAsync(string syncFolder, string message)
    {
        try
        {
            var dir = Path.Combine(syncFolder, "Logs");
            Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(Path.Combine(dir, "sync.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    private static IEnumerable<string> EnumerateBackupFiles(string localPath)
    {
        if (!Directory.Exists(localPath)) yield break;
        foreach (var file in Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    private static string ComputeFilesChecksum(IReadOnlyList<string> files, string root)
    {
        using var sha = SHA256.Create();
        foreach (var file in files.OrderBy(x => Path.GetRelativePath(root, x), StringComparer.OrdinalIgnoreCase))
        {
            var relative = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/'));
            sha.TransformBlock(relative, 0, relative.Length, null, 0);
            using var stream = File.OpenRead(file);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash ?? []);
    }
}
