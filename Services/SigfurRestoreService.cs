using System.IO.Compression;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class SigfurRestoreService
{
    private readonly SigfurBackupService _backupService;

    public SigfurRestoreService(SigfurBackupService backupService)
    {
        _backupService = backupService;
    }

    public async Task RestoreLatestBackupAsync(SigfurProfileSession session, bool createSafetyBackup, CancellationToken ct = default)
    {
        if (!session.IsProfileMode || session.Config is null)
            throw new InvalidOperationException("Restauração exige um Perfil SIGFUR ativo.");
        var syncFolder = session.Config.SyncFolderPath;
        var localPath = session.Config.LocalDataPath;
        var backupPath = Path.Combine(syncFolder, "ultimo.sigfurbak");
        if (!File.Exists(backupPath))
            throw new FileNotFoundException("Nenhum backup foi encontrado na pasta de sincronização escolhida.", backupPath);

        await SigfurBackupService.WriteLogAsync(syncFolder, $"Início da restauração | destino={localPath} | backup={backupPath}");
        SigfurBackupService.ValidateBackup(backupPath);
        var metadata = await _backupService.ReadMetadataAsync(backupPath) ?? new SigfurBackupMetadata
        {
            ProfileName = session.Profile?.ProfileName ?? session.Config.ProfileName,
            CreatedAt = File.GetLastWriteTime(backupPath),
            SourcePath = backupPath,
            MachineName = "desconhecido"
        };

        var tempRoot = Path.Combine(Path.GetTempPath(), "SIGFUR_RESTORE_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            ZipFile.ExtractToDirectory(backupPath, tempRoot);

            var layout = DetectExtractedDataLayout(tempRoot)
                ?? throw new InvalidDataException("Backup encontrado, mas não contém arquivos reconhecidos do SIGFUR.");

            await SigfurBackupService.WriteLogAsync(syncFolder, $"Formato detectado: {layout.DisplayName} | raizDados={layout.DataPath}");

            var extractedFiles = Directory.EnumerateFiles(layout.DataPath, "*", SearchOption.AllDirectories)
                .Where(file => !ShouldSkipArchiveControlFile(layout.DataPath, file))
                .ToList();

            if (extractedFiles.Count == 0)
                throw new InvalidDataException("Backup encontrado, mas não contém arquivos reconhecidos do SIGFUR.");

            string? safetyBackup = null;
            if (createSafetyBackup && Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any())
                safetyBackup = CreateSafetyBackup(localPath);

            ReplaceLocalDataSafely(layout.DataPath, localPath, ct);
            await SigfurBackupService.WriteLogAsync(syncFolder, $"Fim da restauração | perfil={metadata.ProfileName} | arquivos={extractedFiles.Count} | formato={layout.DisplayName} | safety={safetyBackup ?? "não criado"}");
        }
        catch (Exception ex)
        {
            await SigfurBackupService.WriteLogAsync(syncFolder, "Erro na restauração | " + ex.Message);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    public string CreateSafetyBackup(string localPath)
    {
        var destination = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SIGFUR_RESTORE_SAFETY_BACKUP",
            DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        CopyDirectory(localPath, destination, CancellationToken.None);
        return destination;
    }

    public Task<string> CreateSafetyBackupAsync(string localPath)
        => Task.Run(() => Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any()
            ? CreateSafetyBackup(localPath)
            : string.Empty);

    public async Task<bool> HasLocalDataNewerThanBackupAsync(string localPath, string syncFolder)
    {
        var info = await _backupService.GetLastBackupInfoAsync(syncFolder);
        if (info is null || !Directory.Exists(localPath)) return false;
        var newestLocal = Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                           && !path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
                           && !path.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
            .Select(File.GetLastWriteTime)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        return newestLocal > info.LastWriteTime.AddMinutes(2);
    }

    private static BackupDataLayout? DetectExtractedDataLayout(string extractedRoot)
    {
        if (!Directory.Exists(extractedRoot)) return null;

        var explicitData = FindImmediateDirectory(extractedRoot, "data");
        if (explicitData is not null && ContainsSigfurData(explicitData))
            return new BackupDataLayout(explicitData, "Data/");

        if (ContainsSigfurData(extractedRoot))
            return new BackupDataLayout(extractedRoot, "raiz do backup");

        foreach (var candidate in EnumerateLikelyDataDirectories(extractedRoot))
        {
            if (ContainsSigfurData(candidate))
                return new BackupDataLayout(candidate, "estrutura aninhada");
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLikelyDataDirectories(string root)
    {
        var candidates = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(ScoreCandidateDirectory)
            .ThenBy(path => path.Length)
            .Take(250);

        foreach (var candidate in candidates)
            yield return candidate;
    }

    private static int ScoreCandidateDirectory(string path)
    {
        var name = Path.GetFileName(path);
        var normalized = path.Replace('\\', '/');
        var score = 0;
        if (name.Equals("data", StringComparison.OrdinalIgnoreCase)) score += 100;
        if (name.Equals("dados", StringComparison.OrdinalIgnoreCase)) score += 80;
        if (normalized.Contains("/SIGFUR/", StringComparison.OrdinalIgnoreCase)) score += 25;
        if (normalized.Contains("/Perfis/", StringComparison.OrdinalIgnoreCase)) score += 15;
        return score;
    }

    private static string? FindImmediateDirectory(string root, string name)
        => Directory.EnumerateDirectories(root)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsSigfurData(string directory)
    {
        if (!Directory.Exists(directory)) return false;

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

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (knownFiles.Contains(Path.GetFileName(file)))
                    return true;
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (knownDirectories.Contains(Path.GetFileName(child)))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void ReplaceLocalDataSafely(string extractedData, string localPath, CancellationToken ct)
    {
        Directory.CreateDirectory(localPath);
        var staging = localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "_restore_staging_" + Guid.NewGuid().ToString("N");
        CopyDirectory(extractedData, staging, ct, skipArchiveControlFiles: true);

        var old = localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "_restore_old_" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(localPath))
                Directory.Move(localPath, old);
            Directory.Move(staging, localPath);
            try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
        }
        catch
        {
            try
            {
                if (!Directory.Exists(localPath) && Directory.Exists(old))
                    Directory.Move(old, localPath);
            }
            catch { }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct, bool skipArchiveControlFiles = false)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (skipArchiveControlFiles && ShouldSkipArchiveControlFile(source, file)) continue;
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static bool ShouldSkipArchiveControlFile(string source, string file)
    {
        var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
        return relative.Equals("metadata.json", StringComparison.OrdinalIgnoreCase)
               || relative.Equals(ProfileService.ProfileFileName, StringComparison.OrdinalIgnoreCase)
               || relative.EndsWith(".sigfurbak", StringComparison.OrdinalIgnoreCase)
               || relative.StartsWith("Backups/", StringComparison.OrdinalIgnoreCase)
               || relative.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BackupDataLayout(string DataPath, string DisplayName);
}
