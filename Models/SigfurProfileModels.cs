namespace SIGFUR.Wpf.Models;

public sealed class SigfurProfile
{
    public string ProfileName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastUpdatedAt { get; set; } = DateTime.Now;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string HashAlgorithm { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; } = 100_000;
    public bool BackupEncryptionEnabled { get; set; }
    public string LastBackupFile { get; set; } = "ultimo.sigfurbak";
    public string AppVersion { get; set; } = "SIGFUR WPF";
    public string MachineCreated { get; set; } = Environment.MachineName;
}

public sealed class SigfurProfileConfig
{
    public string ProfileName { get; set; } = string.Empty;
    public string LocalDataPath { get; set; } = string.Empty;
    public string SyncFolderPath { get; set; } = string.Empty;
    public bool BackupOnClose { get; set; }
    public bool CreateSafetyBackupBeforeRestore { get; set; } = true;
    public DateTime LinkedAt { get; set; } = DateTime.Now;
    public DateTime LastUsedAt { get; set; } = DateTime.Now;
}

public sealed class SigfurProfileSession
{
    public bool IsProfileMode { get; set; }
    public SigfurProfile? Profile { get; set; }
    public SigfurProfileConfig? Config { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string LocalDataPath => Config?.LocalDataPath ?? string.Empty;
    public string SyncFolderPath => Config?.SyncFolderPath ?? string.Empty;
}

public sealed class SigfurBackupMetadata
{
    public string ProfileName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string BackupMode { get; set; } = "Rápido";
    public string MachineName { get; set; } = Environment.MachineName;
    public string AppVersion { get; set; } = "SIGFUR WPF";
    public string DatabaseVersion { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
}

public enum SigfurBackupMode
{
    Quick,
    Complete
}

public sealed class SigfurBackupOptions
{
    public SigfurBackupMode Mode { get; set; } = SigfurBackupMode.Quick;
    public bool IncludePhotos { get; set; } = true;
    public bool IncludeBulletins { get; set; } = true;
    public bool IncludeAditaments { get; set; } = true;
    public bool IncludePaystubs { get; set; } = true;
    public bool IncludeFinancialStatements { get; set; } = true;
    public bool IncludeGeneratedDocuments { get; set; } = true;
    public bool IncludeLegislation { get; set; } = true;
    public bool IncludeHashes { get; set; }

    public static SigfurBackupOptions Quick() => new() { Mode = SigfurBackupMode.Quick };
    public static SigfurBackupOptions CompleteDefault() => new()
    {
        Mode = SigfurBackupMode.Complete,
        IncludePhotos = true,
        IncludeBulletins = true,
        IncludeAditaments = true,
        IncludePaystubs = true,
        IncludeFinancialStatements = true,
        IncludeGeneratedDocuments = true,
        IncludeLegislation = true
    };
}

public sealed class SigfurBackupEstimate
{
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public List<string> IncludedRoots { get; set; } = [];
    public List<string> ExcludedRoots { get; set; } = [];
    public string Display => $"{FileCount:N0} arquivo(s) - {TotalBytes / 1024d / 1024d:N1} MB";
}

public sealed class SigfurBackupProgress
{
    public string Stage { get; set; } = string.Empty;
    public string CurrentFile { get; set; } = string.Empty;
    public int ProcessedFiles { get; set; }
    public int TotalFiles { get; set; }
    public long ProcessedBytes { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class SigfurBackupManifestEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string Hash { get; set; } = string.Empty;
}

public sealed class SigfurBackupReport
{
    public List<string> IncludedFiles { get; set; } = [];
    public List<string> ExcludedFiles { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

public sealed class SigfurLastBackupInfo
{
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Type { get; set; } = string.Empty;
    public long FinalSize { get; set; }
    public string Path { get; set; } = string.Empty;
    public string Status { get; set; } = "concluído";
}

public sealed class SigfurBackupInfo
{
    public string FilePath { get; set; } = string.Empty;
    public SigfurBackupMetadata? Metadata { get; set; }
    public DateTime LastWriteTime { get; set; }
    public long Length { get; set; }
    public string Display => string.IsNullOrWhiteSpace(FilePath)
        ? "Nenhum backup encontrado"
        : $"{Path.GetFileName(FilePath)} - {LastWriteTime:dd/MM/yyyy HH:mm} - {Length / 1024d / 1024d:N1} MB";
}
