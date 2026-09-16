using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class SigfurBackupService
{
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public async Task<SigfurBackupInfo> CreateBackupAsync(SigfurProfileSession session, CancellationToken ct = default)
        => await CreateBackupAsync(session, SigfurBackupOptions.CompleteDefault(), null, ct);

    public async Task<SigfurBackupEstimate> EstimateAsync(SigfurProfileSession session, SigfurBackupOptions options, CancellationToken ct = default)
    {
        if (!session.IsProfileMode || session.Config is null)
            throw new InvalidOperationException("Estimativa de backup exige um Perfil SIGFUR ativo.");

        return await Task.Run(() =>
        {
            var plan = BuildBackupPlan(session.Config.LocalDataPath, options, ct);
            return new SigfurBackupEstimate
            {
                FileCount = plan.Files.Count,
                TotalBytes = plan.Files.Sum(x => x.Size),
                IncludedRoots = plan.Files.Select(x => FirstSegment(x.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
                ExcludedRoots = plan.Excluded.Select(FirstSegment).Distinct(StringComparer.OrdinalIgnoreCase).Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x).Take(80).ToList()
            };
        }, ct);
    }

    public async Task<SigfurBackupInfo> CreateBackupAsync(
        SigfurProfileSession session,
        SigfurBackupOptions options,
        IProgress<SigfurBackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!session.IsProfileMode || session.Profile is null || session.Config is null)
            throw new InvalidOperationException("Backup de sincronização exige um Perfil SIGFUR ativo.");

        var localPath = session.Config.LocalDataPath;
        var syncPath = session.Config.SyncFolderPath;
        if (!Directory.Exists(localPath)) throw new DirectoryNotFoundException("Pasta local dos dados não encontrada: " + localPath);
        Directory.CreateDirectory(syncPath);
        Directory.CreateDirectory(Path.Combine(syncPath, "Backups"));
        Directory.CreateDirectory(Path.Combine(syncPath, "Logs"));

        var modeText = options.Mode == SigfurBackupMode.Complete ? "Completo" : "Rápido";
        await WriteLogAsync(syncPath, $"Início do backup {modeText} | perfil={session.Profile.ProfileName} | local={localPath}");

        var temp = Path.Combine(syncPath, "backup_temp_" + Guid.NewGuid().ToString("N") + ".sigfurbak");
        var latest = Path.Combine(syncPath, "ultimo.sigfurbak");
        var versioned = Path.Combine(syncPath, "Backups", $"SIGFUR_BACKUP_{modeText.ToUpperInvariant()}_{DateTime.Now:yyyy-MM-dd_HHmm}.sigfurbak");
        var metadata = new SigfurBackupMetadata
        {
            ProfileName = session.Profile.ProfileName,
            CreatedAt = DateTime.Now,
            BackupMode = modeText,
            SourcePath = localPath,
            MachineName = Environment.MachineName
        };

        try
        {
            var importedPhotos = await Task.Run(() => ImportExternalPhotoFiles(localPath, ct), ct);
            if (importedPhotos > 0)
                await WriteLogAsync(syncPath, $"Fotos externas copiadas para a pasta de dados antes do backup: {importedPhotos}");

            var plan = await Task.Run(() => BuildBackupPlan(localPath, options, ct), ct);
            metadata.TotalFiles = plan.Files.Count;
            metadata.TotalBytes = plan.Files.Sum(x => x.Size);
            progress?.Report(new SigfurBackupProgress { Stage = "Preparando pacote", TotalFiles = plan.Files.Count, TotalBytes = metadata.TotalBytes });

            using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                archive.CreateEntry("data/");
                var processed = 0;
                long processedBytes = 0;
                foreach (var item in plan.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        AddFileShared(archive, item.FullPath, "data/" + item.RelativePath);
                        processed++;
                        processedBytes += item.Size;
                        progress?.Report(new SigfurBackupProgress
                        {
                            Stage = "Compactando",
                            CurrentFile = item.RelativePath,
                            ProcessedFiles = processed,
                            TotalFiles = plan.Files.Count,
                            ProcessedBytes = processedBytes,
                            TotalBytes = metadata.TotalBytes
                        });
                    }
                    catch (Exception ex)
                    {
                        plan.Report.Errors.Add($"{item.RelativePath}: {ex.Message}");
                    }
                }

                await WriteJsonEntryAsync(archive, "metadata.json", metadata, ct);
                await WriteJsonEntryAsync(archive, "backup_manifest.json", plan.Manifest, ct);
                await WriteJsonEntryAsync(archive, "backup_report.json", plan.Report, ct);
            }

            ValidateBackup(temp);
            File.Copy(temp, versioned, true);
            File.Move(temp, latest, true);

            await WriteLastBackupInfoAsync(syncPath, new SigfurLastBackupInfo
            {
                CreatedAt = DateTime.Now,
                Type = modeText,
                FinalSize = new FileInfo(latest).Length,
                Path = latest,
                Status = "concluído"
            });

            session.Profile.LastUpdatedAt = DateTime.Now;
            session.Profile.LastBackupFile = "ultimo.sigfurbak";
            await new JsonFileService().SaveAsync(ProfileService.ProfileMetadataPath(syncPath), session.Profile);

            var info = await GetLastBackupInfoAsync(syncPath);
            await WriteLogAsync(syncPath, $"Fim do backup {modeText} | arquivo={Path.GetFileName(latest)} | arquivos={metadata.TotalFiles} | bytes={metadata.TotalBytes}");
            return info ?? new SigfurBackupInfo { FilePath = latest, Metadata = metadata, LastWriteTime = File.GetLastWriteTime(latest), Length = new FileInfo(latest).Length };
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            await WriteLastBackupInfoAsync(syncPath, new SigfurLastBackupInfo { CreatedAt = DateTime.Now, Type = modeText, Path = latest, Status = "cancelado" });
            await WriteLogAsync(syncPath, "Backup cancelado pelo usuário.");
            throw;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            await WriteLastBackupInfoAsync(syncPath, new SigfurLastBackupInfo { CreatedAt = DateTime.Now, Type = modeText, Path = latest, Status = "falhou" });
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

    private async Task WriteJsonEntryAsync<T>(ZipArchive archive, string name, T value, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, ct);
    }

    private static BackupPlan BuildBackupPlan(string localPath, SigfurBackupOptions options, CancellationToken ct)
    {
        var plan = new BackupPlan();
        if (!Directory.Exists(localPath)) return plan;

        foreach (var file in Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var relative = Path.GetRelativePath(localPath, file).Replace('\\', '/');
            if (ShouldExclude(relative, name, options, out var reason))
            {
                plan.Excluded.Add(relative);
                plan.Report.ExcludedFiles.Add($"{relative} | {reason}");
                continue;
            }

            try
            {
                var info = new FileInfo(file);
                var hash = options.IncludeHashes ? ComputeFileHash(file) : string.Empty;
                plan.Files.Add(new BackupPlanFile(file, relative, info.Length));
                plan.Manifest.Add(new SigfurBackupManifestEntry
                {
                    RelativePath = relative,
                    Type = Classify(relative),
                    Size = info.Length,
                    ModifiedAt = info.LastWriteTime,
                    Hash = hash
                });
                plan.Report.IncludedFiles.Add(relative);
            }
            catch (Exception ex)
            {
                plan.Report.Errors.Add($"{relative}: {ex.Message}");
            }
        }

        return plan;
    }

    private static bool ShouldExclude(string relative, string fileName, SigfurBackupOptions options, out string reason)
    {
        reason = string.Empty;
        var first = FirstSegment(relative);
        var ext = Path.GetExtension(fileName);
        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-journal", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".sigfurbak", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            reason = "arquivo temporário, controle ou backup antigo";
            return true;
        }

        if (first.Equals("backups", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Backups", StringComparison.OrdinalIgnoreCase)
            || first.Equals("Cache", StringComparison.OrdinalIgnoreCase)
            || first.Equals("logs", StringComparison.OrdinalIgnoreCase)
            || first.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || first.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || first.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || first.Equals("database_safety", StringComparison.OrdinalIgnoreCase)
            || first.Contains("browser_profile", StringComparison.OrdinalIgnoreCase)
            || first.Contains("selenium", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("_tmp/", StringComparison.OrdinalIgnoreCase))
        {
            reason = "pasta excluída por regra de segurança";
            return true;
        }

        if (options.Mode == SigfurBackupMode.Quick)
        {
            var essential = ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".db", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".dat", StringComparison.OrdinalIgnoreCase)
                            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
            if (!essential || relative.Contains('/'))
            {
                reason = "fora do backup rápido";
                return true;
            }
            return false;
        }

        if (!options.IncludePaystubs && first.Equals("contracheques", StringComparison.OrdinalIgnoreCase))
        {
            reason = "contracheques desmarcados";
            return true;
        }
        if (!options.IncludeBulletins && (first.StartsWith("boletins_salvos", StringComparison.OrdinalIgnoreCase) || first.Equals("boletins_externos", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "boletins desmarcados";
            return true;
        }
        if (!options.IncludeAditaments && first.Equals("boletim_furriel", StringComparison.OrdinalIgnoreCase))
        {
            reason = "aditamentos desmarcados";
            return true;
        }
        if (!options.IncludeGeneratedDocuments && (first.Equals("documentos_gerados", StringComparison.OrdinalIgnoreCase) || first.Equals("documentos_militares", StringComparison.OrdinalIgnoreCase) || first.Equals("phpm", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "documentos desmarcados";
            return true;
        }
        if (!options.IncludeLegislation && first.Equals("legislacao", StringComparison.OrdinalIgnoreCase))
        {
            reason = "legislação local desmarcada";
            return true;
        }
        if (!options.IncludePhotos && first.Equals("documentos_militares", StringComparison.OrdinalIgnoreCase) && IsImage(ext))
        {
            reason = "fotos desmarcadas";
            return true;
        }
        if (!options.IncludeFinancialStatements && relative.Contains("Ficha Financeira", StringComparison.OrdinalIgnoreCase))
        {
            reason = "fichas financeiras desmarcadas";
            return true;
        }
        return false;
    }

    private static void AddFileShared(ZipArchive archive, string file, string entryName)
    {
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static string ComputeFileHash(string file)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(sha.ComputeHash(stream));
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
            "EA",
            "boletins",
            "boletins_salvos",
            "boletins_salvos_textos",
            "boletins_salvos_parse",
            "boletins_externos",
            "boletim_furriel",
            "legislacao",
            "documentos_militares",
            "SIPPES",
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

    private async Task WriteLastBackupInfoAsync(string syncPath, SigfurLastBackupInfo info)
    {
        try { await new JsonFileService().SaveAsync(Path.Combine(syncPath, "last_backup_info.json"), info); }
        catch { }
    }

    private static string FirstSegment(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        var index = normalized.IndexOf('/');
        return index < 0 ? normalized : normalized[..index];
    }

    private static bool IsImage(string ext)
        => ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
           || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
           || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
           || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
           || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);

    private static string Classify(string relative)
    {
        var first = FirstSegment(relative);
        if (first.Equals("EA", StringComparison.OrdinalIgnoreCase)) return "Exercicios Anteriores";
        if (first.Equals("contracheques", StringComparison.OrdinalIgnoreCase)) return relative.Contains("Ficha Financeira", StringComparison.OrdinalIgnoreCase) ? "Ficha Financeira" : "Contracheque";
        if (first.Equals("boletim_furriel", StringComparison.OrdinalIgnoreCase)) return "Aditamento do Furriel";
        if (first.Equals("boletins_externos", StringComparison.OrdinalIgnoreCase)) return "Boletim externo";
        if (first.StartsWith("boletins_salvos", StringComparison.OrdinalIgnoreCase)) return "Boletim Inteligente";
        if (first.Equals("documentos_gerados", StringComparison.OrdinalIgnoreCase)) return "Documento gerado";
        if (first.Equals("documentos_militares", StringComparison.OrdinalIgnoreCase)) return "Documento do militar";
        if (first.Equals("legislacao", StringComparison.OrdinalIgnoreCase)) return "Legislação local";
        if (relative.Equals("militares.db", StringComparison.OrdinalIgnoreCase)) return "Banco de dados";
        return "Dados SIGFUR";
    }

    private static int ImportExternalPhotoFiles(string localPath, CancellationToken ct)
    {
        var database = Path.Combine(localPath, "militares.db");
        if (!File.Exists(database)) return 0;

        var photosRoot = Path.Combine(localPath, "documentos_militares", "Fotos");
        Directory.CreateDirectory(photosRoot);

        var imported = 0;
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

            foreach (var table in new[] { "militares", "lt_militares" })
                imported += ImportExternalPhotoFiles(connection, table, localPath, photosRoot, ct);
        }
        catch
        {
            return imported;
        }

        return imported;
    }

    private static int ImportExternalPhotoFiles(SqliteConnection connection, string table, string localPath, string photosRoot, CancellationToken ct)
    {
        if (!SqliteTableExists(connection, table)) return 0;
        var columns = SqliteColumns(connection, table);
        if (!columns.Contains("id") || !columns.Contains("foto")) return 0;

        var nameColumn = columns.Contains("nome") ? "nome" : "''";
        var cpfColumn = columns.Contains("cpf") ? "cpf" : "''";
        var precColumn = columns.Contains("prec_cp") ? "prec_cp" : "''";
        var rows = new List<(long Id, string Name, string Cpf, string Prec, string Photo)>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = $"SELECT id, COALESCE({nameColumn},''), COALESCE({cpfColumn},''), COALESCE({precColumn},''), COALESCE(foto,'') FROM {table} WHERE TRIM(COALESCE(foto,''))<>'';";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                rows.Add((
                    reader.GetInt64(0),
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture) ?? string.Empty));
            }
        }

        var imported = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var source = ResolveExistingFile(row.Photo, localPath);
            if (string.IsNullOrWhiteSpace(source) || IsInsideDirectory(source, localPath)) continue;

            try
            {
                var extension = Path.GetExtension(source);
                if (!IsImage(extension)) continue;
                var identity = FirstNonBlank(row.Cpf, row.Prec, row.Name, row.Id.ToString(CultureInfo.InvariantCulture));
                var fileName = $"{table}_{row.Id:000000}_{SafeFileName(identity)}{extension}";
                var target = UniquePath(photosRoot, fileName);
                File.Copy(source, target, false);

                using var update = connection.CreateCommand();
                update.CommandText = $"UPDATE {table} SET foto=$foto WHERE id=$id;";
                update.Parameters.AddWithValue("$foto", target);
                update.Parameters.AddWithValue("$id", row.Id);
                update.ExecuteNonQuery();
                imported++;
            }
            catch
            {
                // Nao impede o backup dos demais dados.
            }
        }

        return imported;
    }

    private static bool SqliteTableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static HashSet<string> SqliteColumns(SqliteConnection connection, string table)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(Convert.ToString(reader["name"], CultureInfo.InvariantCulture) ?? string.Empty);
        return result;
    }

    private static string? ResolveExistingFile(string value, string localPath)
    {
        var path = (value ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Path.IsPathFullyQualified(path) && File.Exists(path)) return Path.GetFullPath(path);
            var relative = Path.GetFullPath(Path.Combine(localPath, path));
            return File.Exists(relative) ? relative : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsInsideDirectory(string file, string directory)
    {
        try
        {
            var fullFile = Path.GetFullPath(file);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string FirstNonBlank(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "foto";

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        value = Regex.Replace(value, @"\s+", "_", RegexOptions.CultureInvariant).Trim('_', '.', ' ');
        return string.IsNullOrWhiteSpace(value) ? "arquivo" : value[..Math.Min(value.Length, 80)];
    }

    private static string UniquePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; index < 1000; index++)
        {
            path = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(path)) return path;
        }

        return Path.Combine(directory, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
    }

    private sealed class BackupPlan
    {
        public List<BackupPlanFile> Files { get; } = [];
        public List<string> Excluded { get; } = [];
        public List<SigfurBackupManifestEntry> Manifest { get; } = [];
        public SigfurBackupReport Report { get; } = new();
    }

    private sealed record BackupPlanFile(string FullPath, string RelativePath, long Size);
}
