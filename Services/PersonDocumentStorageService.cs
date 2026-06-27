using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class PersonDocumentStorageService
{
    public static string DefaultRoot(AppPaths paths)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents)
            ? paths.PaystubsDirectory
            : Path.Combine(documents, "SIGFUR", "Contracheques");
    }

    public static string ResolveConfiguredRoot(AppPaths paths)
    {
        foreach (var file in new[] { paths.PaystubCenterSettingsFile, paths.CpexPaystubSettingsFile })
        {
            try
            {
                if (!File.Exists(file)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!property.Name.Equals("OutputDirectory", StringComparison.OrdinalIgnoreCase)
                        || property.Value.ValueKind != JsonValueKind.String) continue;
                    var configured = property.Value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(configured)) return configured;
                }
            }
            catch { }
        }
        return DefaultRoot(paths);
    }

    public static string BuildFolder(
        string root,
        string? rank,
        string? name,
        string? cpf,
        string? prec,
        bool external)
    {
        root = string.IsNullOrWhiteSpace(root) ? "." : root.Trim();
        var identifier = MilitaryFormatting.Digits(cpf);
        if (string.IsNullOrWhiteSpace(identifier)) identifier = MilitaryFormatting.Digits(prec);
        var nameToken = Token(string.IsNullOrWhiteSpace(name) ? identifier : name, titleCase: false);
        if (string.IsNullOrWhiteSpace(nameToken)) nameToken = "SEM_NOME";

        if (external)
        {
            var externalToken = string.IsNullOrWhiteSpace(identifier) ? nameToken : $"{nameToken}_{identifier}";
            return Path.Combine(root, "Pessoas_de_Fora", SafeFileName(externalToken));
        }

        var canonicalRank = MilitaryRankService.Canonicalize(rank);
        var rankToken = Token(canonicalRank, titleCase: true);
        if (string.IsNullOrWhiteSpace(rankToken)) rankToken = "Militar";
        return Path.Combine(root, SafeFileName($"{rankToken}_{nameToken}"));
    }

    public static string PrepareRegisteredFolder(
        AppPaths paths,
        string root,
        string? rank,
        string? name,
        string? cpf,
        string? prec)
    {
        var destination = BuildFolder(root, rank, name, cpf, prec, external: false);
        Directory.CreateDirectory(destination);
        var cpfDigits = MilitaryFormatting.Digits(cpf);
        if (string.IsNullOrWhiteSpace(cpfDigits)) return destination;

        foreach (var legacy in new[]
                 {
                     Path.Combine(paths.PaystubsDirectory, cpfDigits),
                     BuildFolder(paths.PaystubsDirectory, rank, name, cpf, prec, external: false),
                     Path.Combine(root, cpfDigits)
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(legacy) || SamePath(legacy, destination)) continue;
            foreach (var source in Directory.EnumerateFiles(legacy, "*.pdf", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var target = UniquePath(destination, Path.GetFileName(source));
                    File.Move(source, target);
                }
                catch { }
            }
        }
        return destination;
    }

    private static string Token(string? value, bool titleCase)
    {
        var text = (value ?? string.Empty)
            .Replace('º', ' ')
            .Replace('°', ' ')
            .Replace('ª', ' ')
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }
        var cleaned = Regex.Replace(builder.ToString(), "_+", "_", RegexOptions.CultureInvariant).Trim('_');
        if (!titleCase) return cleaned.ToUpperInvariant();
        return string.Join('_', cleaned.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length <= 1
                ? part.ToUpperInvariant()
                : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return Regex.Replace(value, "_+", "_", RegexOptions.CultureInvariant).Trim('_', '.', ' ');
    }

    private static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left).TrimEnd('\\', '/')
            .Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string UniquePath(string directory, string fileName)
    {
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
}
