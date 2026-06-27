using System.Diagnostics;

namespace SIGFUR.Wpf.Services;

public static class FileOpenService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".png", ".jpg", ".jpeg", ".webp", ".txt", ".csv", ".rtf"
    };

    public static bool TryOpenFile(string path, out string error)
    {
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) { error = "Caminho vazio."; return false; }
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) { error = "Arquivo não encontrado."; return false; }
            var extension = Path.GetExtension(full);
            if (!AllowedExtensions.Contains(extension)) { error = $"Extensão não permitida para abertura automática: {extension}"; return false; }
            Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static bool TryOpenFolder(string path, out string error)
    {
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) { error = "Caminho vazio."; return false; }
            var full = Path.GetFullPath(path);
            if (File.Exists(full)) full = Path.GetDirectoryName(full) ?? full;
            if (!Directory.Exists(full)) { error = "Pasta não encontrada."; return false; }
            Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static bool TryReveal(string path, out string error)
    {
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) { error = "Caminho vazio."; return false; }
            ShellService.RevealInExplorer(path);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static bool TryOpenUrl(string url, out string error)
    {
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(url)) { error = "URL vazia."; return false; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                error = "URL inválida.";
                return false;
            }
            Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
