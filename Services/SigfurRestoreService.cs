using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
            var repairedPaths = RepairRestoredPaths(localPath, metadata.SourcePath, ct);
            await SigfurBackupService.WriteLogAsync(syncFolder, $"Fim da restauração | perfil={metadata.ProfileName} | arquivos={extractedFiles.Count} | formato={layout.DisplayName} | caminhos_atualizados={repairedPaths} | safety={safetyBackup ?? "não criado"}");
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
            "EA",
            "boletins",
            "boletins_salvos",
            "boletins_salvos_textos",
            "boletins_salvos_parse",
            "boletins_externos",
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

    private static int RepairRestoredPaths(string localPath, string? sourcePath, CancellationToken ct)
    {
        var oldRoot = NormalizeRoot(sourcePath);
        var newRoot = NormalizeRoot(localPath);
        if (string.IsNullOrWhiteSpace(oldRoot) || string.IsNullOrWhiteSpace(newRoot) || SamePath(oldRoot, newRoot))
            return 0;

        var replacements = BuildPathReplacements(oldRoot, newRoot);
        var changed = 0;
        changed += RepairSqliteDatabase(Path.Combine(localPath, "militares.db"), replacements, ct);
        changed += RepairJsonFiles(localPath, replacements, ct);
        return changed;
    }

    private static int RepairSqliteDatabase(string database, IReadOnlyList<(string Old, string New)> replacements, CancellationToken ct)
    {
        if (!File.Exists(database)) return 0;
        var changed = 0;

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 15
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=15000;";
                pragma.ExecuteNonQuery();
            }

            foreach (var table in ListTables(connection))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var column in ListTextColumns(connection, table))
                foreach (var (oldValue, newValue) in replacements)
                {
                    if (string.IsNullOrWhiteSpace(oldValue) || oldValue.Equals(newValue, StringComparison.Ordinal)) continue;
                    using var update = connection.CreateCommand();
                    update.CommandText = $"UPDATE {QuoteIdentifier(table)} SET {QuoteIdentifier(column)}=replace({QuoteIdentifier(column)},$old,$new) WHERE instr({QuoteIdentifier(column)},$old)>0;";
                    update.Parameters.AddWithValue("$old", oldValue);
                    update.Parameters.AddWithValue("$new", newValue);
                    changed += update.ExecuteNonQuery();
                }
            }
        }
        catch
        {
            return changed;
        }

        return changed;
    }

    private static int RepairJsonFiles(string localPath, IReadOnlyList<(string Old, string New)> replacements, CancellationToken ct)
    {
        if (!Directory.Exists(localPath)) return 0;
        var changed = 0;

        foreach (var file in Directory.EnumerateFiles(localPath, "*.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var original = File.ReadAllText(file, Encoding.UTF8);
                var updated = ApplyReplacements(original, replacements);
                if (updated.Equals(original, StringComparison.Ordinal)) continue;
                File.WriteAllText(file, updated, Encoding.UTF8);
                changed++;
            }
            catch
            {
                // Um JSON travado nao deve desfazer a restauracao inteira.
            }
        }

        return changed;
    }

    private static string ApplyReplacements(string text, IReadOnlyList<(string Old, string New)> replacements)
    {
        foreach (var (oldValue, newValue) in replacements)
        {
            if (!string.IsNullOrWhiteSpace(oldValue) && !oldValue.Equals(newValue, StringComparison.Ordinal))
                text = text.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        return text;
    }

    private static IReadOnlyList<(string Old, string New)> BuildPathReplacements(string oldRoot, string newRoot)
    {
        var pairs = new List<(string Old, string New)>();
        Add(oldRoot, newRoot);
        Add(oldRoot.Replace('\\', '/'), newRoot.Replace('\\', '/'));
        Add(JsonEscaped(oldRoot), JsonEscaped(newRoot));
        Add(JsonEscaped(oldRoot.Replace('\\', '/')), JsonEscaped(newRoot.Replace('\\', '/')));
        return pairs;

        void Add(string oldValue, string newValue)
        {
            if (string.IsNullOrWhiteSpace(oldValue)) return;
            if (pairs.Any(x => x.Old.Equals(oldValue, StringComparison.Ordinal))) return;
            pairs.Add((oldValue, newValue));
        }
    }

    private static string JsonEscaped(string value)
    {
        var json = JsonSerializer.Serialize(value ?? string.Empty) ?? "\"\"";
        return json.Length >= 2 ? json[1..^1] : value ?? string.Empty;
    }

    private static string NormalizeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path.Trim().Trim('"').TrimEnd('\\', '/'); }
    }

    private static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return left.TrimEnd('\\', '/').Equals(right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
    }

    private static List<string> ListTables(SqliteConnection connection)
    {
        var result = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    private static List<string> ListTextColumns(SqliteConnection connection, string table)
    {
        var result = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = Convert.ToString(reader["name"], CultureInfo.InvariantCulture) ?? string.Empty;
            var type = Convert.ToString(reader["type"], CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (type.Length == 0
                || type.Contains("TEXT", StringComparison.OrdinalIgnoreCase)
                || type.Contains("CHAR", StringComparison.OrdinalIgnoreCase)
                || type.Contains("CLOB", StringComparison.OrdinalIgnoreCase))
                result.Add(name);
        }
        return result;
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + (identifier ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record BackupDataLayout(string DataPath, string DisplayName);
}
