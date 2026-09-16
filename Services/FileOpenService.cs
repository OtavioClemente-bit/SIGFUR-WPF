using System.Diagnostics;

namespace SIGFUR.Wpf.Services;

public static class FileOpenService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".png", ".jpg", ".jpeg", ".webp", ".txt", ".md", ".csv", ".rtf"
    };

    public static bool TryOpenFile(string path, out string error)
    {
        error = string.Empty;
        string? full = null;
        string extension = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) { error = "Caminho vazio."; return false; }
            full = Path.GetFullPath(path);
            if (!File.Exists(full)) { error = "Arquivo não encontrado."; return false; }
            extension = Path.GetExtension(full);
            if (!AllowedExtensions.Contains(extension)) { error = $"Extensão não permitida para abertura automática: {extension}"; return false; }
            var process = Process.Start(new ProcessStartInfo { FileName = full, UseShellExecute = true });
            if (process is null) throw new InvalidOperationException("O Windows não iniciou o aplicativo associado ao arquivo.");
            return true;
        }
        catch (Exception ex)
        {
            // É comum o Windows manter .pdf associado a um Adobe/Acrobat que já foi
            // desinstalado. Nesse caso o ShellExecute falha mesmo com o PDF íntegro.
            // O Edge é um leitor disponível nas instalações suportadas do Windows e
            // oferece uma saída segura sem alterar a associação padrão do usuário.
            var fallbackError = string.Empty;
            if (!string.IsNullOrWhiteSpace(full)
                && extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                && TryOpenPdfWithEdge(full, out fallbackError))
            {
                return true;
            }

            error = string.IsNullOrWhiteSpace(fallbackError)
                ? ex.Message
                : $"{ex.Message} Alternativa pelo Microsoft Edge: {fallbackError}";
            return false;
        }
    }

    public static bool TryOpenFileAtPage(string path, int page, out string error)
    {
        if (page <= 0 || !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return TryOpenFile(path, out error);

        error = string.Empty;
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) { error = "Arquivo não encontrado."; return false; }
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
            };
            var edge = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(edge)) return TryOpenFile(full, out error);

            var start = new ProcessStartInfo { FileName = edge, UseShellExecute = false };
            start.ArgumentList.Add(new Uri(full).AbsoluteUri + "#page=" + page.ToString(CultureInfo.InvariantCulture));
            if (Process.Start(start) is null) throw new InvalidOperationException("O Microsoft Edge não iniciou.");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryOpenPdfWithEdge(string fullPath, out string error)
    {
        error = string.Empty;
        try
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
            };
            var edge = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(edge))
            {
                error = "Microsoft Edge não localizado.";
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = edge,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(fullPath);
            var process = Process.Start(startInfo);
            if (process is null)
            {
                error = "O Microsoft Edge não iniciou.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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
