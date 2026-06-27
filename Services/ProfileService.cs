using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public sealed class ProfileService
{
    public const string ProfileFileName = "profile.sigfurprofile";
    private const string LocalProfilesFileName = "sigfur_profiles.json";
    private readonly JsonFileService _json;
    private readonly SecurityService _security;

    public ProfileService(JsonFileService json, SecurityService security)
    {
        _json = json;
        _security = security;
    }

    public static string ProfileStoreDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SIGFUR", "profiles");

    public static string DefaultLocalDataPath => AppPaths.GetDefaultDataDirectory();

    public static string GetDefaultSyncFolder(string profileName)
    {
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        var root = !string.IsNullOrWhiteSpace(oneDrive) && Directory.Exists(oneDrive)
            ? oneDrive
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(root, "SIGFUR_SYNC", SafeName(string.IsNullOrWhiteSpace(profileName) ? "MeuPerfil" : profileName));
    }

    public async Task<List<SigfurProfileConfig>> LoadLocalProfilesAsync()
    {
        var profiles = await _json.LoadAsync<List<SigfurProfileConfig>>(LocalProfilesPath()) ?? [];
        return profiles
            .Where(x => !string.IsNullOrWhiteSpace(x.ProfileName))
            .OrderBy(x => x.ProfileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<SigfurProfile?> LoadProfileMetadataAsync(string syncFolder)
    {
        var path = ProfileMetadataPath(syncFolder);
        return await _json.LoadAsync<SigfurProfile>(path);
    }

    public async Task<List<SigfurProfile>> LoadProfileFromSyncFolderAsync(string syncFolder)
    {
        var profile = await LoadProfileMetadataAsync(syncFolder);
        return profile is null ? [] : [profile];
    }

    public async Task<SigfurProfileSession> CreateProfileAsync(
        string profileName,
        string password,
        string localDataPath,
        string syncFolder,
        bool backupOnClose,
        bool createSafetyBackupBeforeRestore)
    {
        ValidateProfileInput(profileName, password, localDataPath, syncFolder);
        Directory.CreateDirectory(localDataPath);
        EnsureDirectory(syncFolder);

        var hash = _security.CreatePasswordHash(password);
        var profile = new SigfurProfile
        {
            ProfileName = profileName.Trim(),
            CreatedAt = DateTime.Now,
            LastUpdatedAt = DateTime.Now,
            PasswordHash = hash.Hash,
            PasswordSalt = hash.Salt,
            Iterations = hash.Iterations,
            MachineCreated = Environment.MachineName
        };
        var config = new SigfurProfileConfig
        {
            ProfileName = profile.ProfileName,
            LocalDataPath = Path.GetFullPath(localDataPath),
            SyncFolderPath = Path.GetFullPath(syncFolder),
            BackupOnClose = backupOnClose,
            CreateSafetyBackupBeforeRestore = createSafetyBackupBeforeRestore,
            LinkedAt = DateTime.Now,
            LastUsedAt = DateTime.Now
        };

        Directory.CreateDirectory(Path.Combine(config.SyncFolderPath, "Backups"));
        Directory.CreateDirectory(Path.Combine(config.SyncFolderPath, "Logs"));
        await _json.SaveAsync(ProfileMetadataPath(config.SyncFolderPath), profile);
        await UpsertLocalProfileAsync(config);
        return new SigfurProfileSession { IsProfileMode = true, Profile = profile, Config = config, StatusMessage = "Perfil SIGFUR criado." };
    }

    public async Task<SigfurProfileSession> ValidateLocalProfileAsync(SigfurProfileConfig config, string password)
    {
        var profile = await LoadProfileMetadataAsync(config.SyncFolderPath)
                      ?? throw new InvalidOperationException("Arquivo profile.sigfurprofile não encontrado na pasta de sincronização.");
        if (!string.Equals(profile.ProfileName, config.ProfileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O perfil local não corresponde ao arquivo da pasta de sincronização.");
        if (!_security.VerifyPassword(password, profile.PasswordHash, profile.PasswordSalt, profile.Iterations))
            throw new UnauthorizedAccessException("Senha incorreta para este perfil.");
        config.LastUsedAt = DateTime.Now;
        await UpsertLocalProfileAsync(config);
        return new SigfurProfileSession { IsProfileMode = true, Profile = profile, Config = config, StatusMessage = "Perfil validado." };
    }

    public async Task<SigfurProfileSession> LinkExistingProfileAsync(string syncFolder, string password, string? localDataPath = null)
    {
        EnsureDirectory(syncFolder, createIfMissing: false);
        var profile = await LoadProfileMetadataAsync(syncFolder)
                      ?? throw new InvalidOperationException("Nenhum profile.sigfurprofile foi encontrado na pasta escolhida.");
        if (!_security.VerifyPassword(password, profile.PasswordHash, profile.PasswordSalt, profile.Iterations))
            throw new UnauthorizedAccessException("Senha incorreta para este perfil.");

        var localPath = string.IsNullOrWhiteSpace(localDataPath)
            ? Path.Combine(AppPaths.GetDefaultDataDirectory(), "Perfis", SafeName(profile.ProfileName))
            : localDataPath;
        Directory.CreateDirectory(localPath);
        var config = new SigfurProfileConfig
        {
            ProfileName = profile.ProfileName,
            LocalDataPath = Path.GetFullPath(localPath),
            SyncFolderPath = Path.GetFullPath(syncFolder),
            BackupOnClose = true,
            CreateSafetyBackupBeforeRestore = true,
            LinkedAt = DateTime.Now,
            LastUsedAt = DateTime.Now
        };
        await UpsertLocalProfileAsync(config);
        return new SigfurProfileSession { IsProfileMode = true, Profile = profile, Config = config, StatusMessage = "Perfil existente conectado." };
    }

    public async Task UpsertLocalProfileAsync(SigfurProfileConfig config)
    {
        var profiles = await LoadLocalProfilesAsync();
        profiles.RemoveAll(x => x.ProfileName.Equals(config.ProfileName, StringComparison.OrdinalIgnoreCase));
        profiles.Add(config);
        await _json.SaveAsync(LocalProfilesPath(), profiles.OrderBy(x => x.ProfileName).ToList());
    }

    public async Task UpdateLocalProfileAsync(SigfurProfileConfig config) => await UpsertLocalProfileAsync(config);

    public static string ProfileMetadataPath(string syncFolder) => Path.Combine(syncFolder, ProfileFileName);

    private static string LocalProfilesPath()
    {
        Directory.CreateDirectory(ProfileStoreDirectory);
        return Path.Combine(ProfileStoreDirectory, LocalProfilesFileName);
    }

    private static void ValidateProfileInput(string profileName, string password, string localDataPath, string syncFolder)
    {
        if (string.IsNullOrWhiteSpace(profileName)) throw new InvalidOperationException("Nome do perfil obrigatório.");
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Senha obrigatória.");
        if (string.IsNullOrWhiteSpace(localDataPath)) throw new InvalidOperationException("Pasta local dos dados obrigatória.");
        if (string.IsNullOrWhiteSpace(syncFolder)) throw new InvalidOperationException("Pasta de sincronização obrigatória.");
    }

    private static void EnsureDirectory(string path, bool createIfMissing = true)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Pasta obrigatória.");
        if (Directory.Exists(path)) return;
        if (!createIfMissing) throw new DirectoryNotFoundException("Pasta não encontrada: " + path);
        Directory.CreateDirectory(path);
    }

    private static string SafeName(string value)
        => string.Concat((value ?? string.Empty).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
}
