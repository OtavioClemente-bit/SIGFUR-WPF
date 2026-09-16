using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SIGFUR.Wpf.Services;

/// <summary>Cache local de interpretações semânticas. A chave inclui o conteúdo real do arquivo.</summary>
public sealed class AssistantInterpretationCache
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AssistantInterpretationCache(AppPaths paths)
    {
        _directory = paths.AssistantInterpretationCacheDirectory;
        Directory.CreateDirectory(_directory);
    }

    public async Task<string> BuildKeyAsync(string filePath, string blockId, string parserVersion,
        string promptVersion, string model, CancellationToken cancellationToken = default)
        => await BuildCompositeKeyAsync([filePath], blockId, parserVersion, promptVersion, model, cancellationToken);

    public async Task<string> BuildCompositeKeyAsync(IEnumerable<string> filePaths, string blockId, string parserVersion,
        string promptVersion, string model, CancellationToken cancellationToken = default)
    {
        var hashes = new List<string>();
        foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Order())
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException("Arquivo da evidência não encontrado.", filePath);
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            hashes.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
        }
        var keyMaterial = string.Join("\n", hashes.Append(blockId).Append(parserVersion).Append(promptVersion).Append(model.Trim().ToLowerInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyMaterial)));
    }

    public async Task<T?> TryLoadAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return default;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException) { return default; }
        catch (IOException) { return default; }
    }

    public async Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(key);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, value, cancellationToken: cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { _gate.Release(); }
    }

    private string PathFor(string key)
    {
        if (key.Length != 64 || key.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Chave de cache inválida.", nameof(key));
        return Path.Combine(_directory, key + ".json");
    }
}
