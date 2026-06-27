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
    public string MachineName { get; set; } = Environment.MachineName;
    public string AppVersion { get; set; } = "SIGFUR WPF";
    public string DatabaseVersion { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public long TotalBytes { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
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
