using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SIGFUR.Wpf.Services;

public sealed record DatabaseInspection(
    string Path,
    bool Exists,
    bool IsValid,
    bool HasMilitaryTable,
    int MilitaryCount,
    long SizeBytes,
    DateTime LastWriteTime,
    string Integrity,
    string Error)
{
    public string SizeText => FormatSize(SizeBytes);

    private static string FormatSize(long size)
    {
        if (size <= 0) return "—";
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)size;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return index == 0 ? $"{value:0} {units[index]}" : $"{value:0.0} {units[index]}";
    }
}

public sealed record DatabaseSafetyReport(
    DatabaseInspection Official,
    IReadOnlyList<DatabaseInspection> Candidates,
    bool Recovered,
    string RecoverySource,
    string SnapshotPath,
    string Message);

/// <summary>
/// Protege o caminho oficial do banco do SIGFUR no C#.
/// Regras principais:
/// 1. Um banco oficial que já possui militares nunca é substituído automaticamente.
/// 2. Um arquivo vazio não pode prevalecer sobre outro banco válido com dados.
/// 3. Antes da abertura é criado um snapshot SQLite consistente, incluindo transações em WAL.
/// 4. O serviço nunca cria silenciosamente um banco vazio quando nenhum banco válido existe.
/// </summary>
public sealed class DatabaseSafetyService
{
    private readonly AppPaths _paths;
    private readonly LogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DatabaseSafetyService(AppPaths paths, LogService log)
    {
        _paths = paths;
        _log = log;
    }

    public DatabaseSafetyReport? LastReport { get; private set; }
    public string OfficialDatabasePath => _paths.DatabaseFile;

    public async Task<DatabaseSafetyReport> InitializeAsync(string? legacyProjectRoot = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var report = await Task.Run(() => InitializeCore(legacyProjectRoot), cancellationToken);
            LastReport = report;
            await PersistReportAsync(report);
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DatabaseInspection> InspectOfficialAsync(CancellationToken cancellationToken = default)
        => await Task.Run(() => Inspect(_paths.DatabaseFile), cancellationToken);

    /// <summary>
    /// Verifica o banco durante o uso. Quando a sessão começou com dados e o arquivo
    /// oficial passa inesperadamente para zero/ausente, tenta recuperar um snapshot
    /// válido. Um banco que já iniciou vazio não é substituído sem uma origem segura.
    /// </summary>
    public async Task<DatabaseInspection> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        var current = await InspectOfficialAsync(cancellationToken);
        if (current.IsValid && current.HasMilitaryTable && current.MilitaryCount > 0)
        {
            // Se a sessão começou sem dados e recebeu os primeiros cadastros depois,
            // passa a considerar esse estado como saudável e cria a primeira proteção.
            if (LastReport?.Official.MilitaryCount is null or <= 0)
            {
                var snapshot = await CreateSnapshotAsync("first_data", cancellationToken);
                LastReport = new DatabaseSafetyReport(
                    current,
                    LastReport?.Candidates ?? Array.Empty<DatabaseInspection>(),
                    false,
                    string.Empty,
                    snapshot,
                    $"Banco oficial reconhecido com {current.MilitaryCount} militar(es) durante a sessão.");
                await PersistReportAsync(LastReport);
            }
            else
            {
                // Mantém a quantidade saudável mais recente para que uma queda posterior
                // para zero seja reconhecida, sem gerar snapshot a cada atualização.
                LastReport = LastReport with { Official = current };
            }
            return current;
        }

        var previouslyHealthy = LastReport?.Official.IsValid == true
                                && LastReport.Official.MilitaryCount > 0;
        if (!previouslyHealthy) return current;

        await _log.WriteAsync(
            $"Alerta: banco oficial caiu de {LastReport!.Official.MilitaryCount} militar(es) para {Math.Max(0, current.MilitaryCount)}. Iniciando recuperação segura.");
        var recovered = await InitializeAsync(cancellationToken: cancellationToken);
        return recovered.Official;
    }

    public async Task<string> CreateSnapshotAsync(string reason = "manual", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => CreateSnapshotCore(reason), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DatabaseSafetyReport InitializeCore(string? legacyProjectRoot)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        Directory.CreateDirectory(_paths.DatabaseSafetyDirectory);
        Directory.CreateDirectory(_paths.LogDirectory);

        var candidates = CandidatePaths(legacyProjectRoot)
            .Select(Inspect)
            .Where(x => x.Exists)
            .OrderByDescending(x => x.MilitaryCount)
            .ThenByDescending(x => x.SizeBytes)
            .ThenByDescending(x => x.LastWriteTime)
            .ToList();

        var official = Inspect(_paths.DatabaseFile);
        var recovered = false;
        var recoverySource = string.Empty;

        // Banco oficial com dados é sempre soberano. Isso impede troca silenciosa de arquivo.
        if (official.IsValid && official.HasMilitaryTable && official.MilitaryCount > 0)
        {
            var snapshot = CreateSnapshotCore("startup");
            return new DatabaseSafetyReport(
                official, candidates, false, string.Empty, snapshot,
                $"Banco oficial preservado com {official.MilitaryCount} militar(es)." );
        }

        // Só recupera quando o oficial não tem dados e existe uma origem inequivocamente melhor.
        var best = candidates.FirstOrDefault(x =>
            !Path.GetFullPath(x.Path).Equals(Path.GetFullPath(_paths.DatabaseFile), StringComparison.OrdinalIgnoreCase)
            && x.IsValid && x.HasMilitaryTable && x.MilitaryCount > 0);

        if (best is not null)
        {
            ArchiveOfficialIfPresent(official);
            RestoreWithSqliteBackup(best.Path, _paths.DatabaseFile);
            official = Inspect(_paths.DatabaseFile);
            recovered = official.IsValid && official.MilitaryCount > 0;
            recoverySource = recovered ? best.Path : string.Empty;
        }

        var snapshotPath = official.MilitaryCount > 0 ? CreateSnapshotCore(recovered ? "recovered" : "startup") : string.Empty;
        var message = recovered
            ? $"Banco recuperado com segurança: {official.MilitaryCount} militar(es)."
            : official.Exists
                ? "O banco oficial existe, mas não possui militares. Nenhum arquivo foi sobrescrito."
                : "Banco oficial não encontrado. O SIGFUR não criou um banco vazio automaticamente.";

        return new DatabaseSafetyReport(official, candidates, recovered, recoverySource, snapshotPath, message);
    }

    private IEnumerable<string> CandidatePaths(string? legacyProjectRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var knownNames = new[] { "militares.db", "cadastro_militares.db", "sigfur.db" };
        var raw = new List<string?> { _paths.DatabaseFile };

        // Locais usados por versões anteriores. O caminho oficial continua sendo somente
        // %LOCALAPPDATA%\SIGFUR\militares.db; estes itens são apenas fontes de recuperação.
        foreach (var name in knownNames)
        {
            raw.Add(Path.Combine(localAppData, "SIGFUR", name));
            raw.Add(Path.Combine(roamingAppData, "SIGFUR", name));
            raw.Add(Path.Combine(AppContext.BaseDirectory, name));
            raw.Add(Path.Combine(Environment.CurrentDirectory, name));

            if (!string.IsNullOrWhiteSpace(legacyProjectRoot))
            {
                raw.Add(Path.Combine(legacyProjectRoot, name));
                raw.Add(Path.Combine(legacyProjectRoot, "database", name));
                raw.Add(Path.Combine(legacyProjectRoot, "dados", name));
            }
        }

        foreach (var item in raw)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            string full;
            try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(item)); }
            catch { continue; }
            if (seen.Add(full)) yield return full;
        }

        if (Directory.Exists(_paths.DatabaseSafetyDirectory))
        {
            foreach (var snapshot in Directory.EnumerateFiles(_paths.DatabaseSafetyDirectory, "*.db", SearchOption.TopDirectoryOnly))
            {
                var full = Path.GetFullPath(snapshot);
                if (seen.Add(full)) yield return full;
            }
        }
    }

    public DatabaseInspection Inspect(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
                return new DatabaseInspection(full, false, false, false, -1, 0, DateTime.MinValue, "ausente", string.Empty);

            var info = new FileInfo(full);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            string integrity;
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check;";
                integrity = Convert.ToString(check.ExecuteScalar()) ?? "desconhecido";
            }

            bool hasTable;
            using (var table = connection.CreateCommand())
            {
                table.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='militares');";
                hasTable = Convert.ToInt64(table.ExecuteScalar() ?? 0) == 1;
            }

            var count = -1;
            if (hasTable)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM militares;";
                count = Convert.ToInt32(command.ExecuteScalar() ?? 0);
            }

            return new DatabaseInspection(full, true, integrity.Equals("ok", StringComparison.OrdinalIgnoreCase), hasTable,
                count, info.Length, info.LastWriteTime, integrity, string.Empty);
        }
        catch (Exception ex)
        {
            var info = new FileInfo(path);
            return new DatabaseInspection(Path.GetFullPath(path), info.Exists, false, false, -1,
                info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTime : DateTime.MinValue,
                "erro", ex.Message);
        }
    }

    private string CreateSnapshotCore(string reason)
    {
        var sourceInspection = Inspect(_paths.DatabaseFile);
        if (!sourceInspection.IsValid || !sourceInspection.HasMilitaryTable || sourceInspection.MilitaryCount <= 0)
            return string.Empty;

        Directory.CreateDirectory(_paths.DatabaseSafetyDirectory);
        var safeReason = string.Concat(reason.Where(char.IsLetterOrDigit));
        if (string.IsNullOrWhiteSpace(safeReason)) safeReason = "snapshot";
        var destination = Path.Combine(_paths.DatabaseSafetyDirectory,
            $"militares_{safeReason}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");
        BackupDatabase(_paths.DatabaseFile, destination);
        PruneSnapshots(15);
        return destination;
    }

    private void RestoreWithSqliteBackup(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".restoring";
        TryDelete(temp);
        BackupDatabase(source, temp);

        var restored = Inspect(temp);
        if (!restored.IsValid || !restored.HasMilitaryTable || restored.MilitaryCount <= 0)
            throw new InvalidDataException("A cópia restaurada não passou na validação do SQLite.");

        // Evita que arquivos WAL/SHM antigos permaneçam ao lado do banco restaurado.
        // Eles pertencem à versão anterior do arquivo e nunca devem acompanhar a troca.
        TryDelete(target + "-wal");
        TryDelete(target + "-shm");
        File.Move(temp, target, true);
        TryDelete(target + "-wal");
        TryDelete(target + "-shm");
    }

    private static void BackupDatabase(string source, string destination)
    {
        TryDelete(destination);
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 15
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 15
        };
        using var sourceConnection = new SqliteConnection(sourceBuilder.ToString());
        using var destinationConnection = new SqliteConnection(destinationBuilder.ToString());
        sourceConnection.Open();
        destinationConnection.Open();
        sourceConnection.BackupDatabase(destinationConnection);
    }

    private void ArchiveOfficialIfPresent(DatabaseInspection official)
    {
        if (!official.Exists) return;
        Directory.CreateDirectory(_paths.DatabaseSafetyDirectory);
        var archived = Path.Combine(_paths.DatabaseSafetyDirectory,
            $"official_before_recovery_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");
        try { File.Copy(_paths.DatabaseFile, archived, true); }
        catch { /* a restauração ainda valida o destino antes da troca */ }
    }

    private void PruneSnapshots(int keep)
    {
        try
        {
            var files = Directory.EnumerateFiles(_paths.DatabaseSafetyDirectory, "militares_*.db")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            foreach (var old in files.Skip(keep)) TryDelete(old);
        }
        catch { }
    }

    private async Task PersistReportAsync(DatabaseSafetyReport report)
    {
        try
        {
            Directory.CreateDirectory(_paths.LogDirectory);
            var line = $"[{DateTime.Now:O}] {report.Message} Oficial={report.Official.Path}; Militares={report.Official.MilitaryCount}; Recuperado={report.Recovered}; Origem={report.RecoverySource}{Environment.NewLine}";
            await File.AppendAllTextAsync(_paths.DatabaseSafetyLogFile, line);
            var payload = new
            {
                checked_at = DateTime.Now,
                official_path = report.Official.Path,
                official_count = report.Official.MilitaryCount,
                official_integrity = report.Official.Integrity,
                recovered = report.Recovered,
                recovery_source = report.RecoverySource,
                snapshot = report.SnapshotPath,
                message = report.Message,
                candidates = report.Candidates.Select(x => new { path = x.Path, count = x.MilitaryCount, valid = x.IsValid, size = x.SizeBytes })
            };
            await File.WriteAllTextAsync(_paths.DatabaseLocationFile, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            await _log.WriteAsync("Falha ao registrar diagnóstico do banco WPF.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
