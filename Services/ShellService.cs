using System.Diagnostics;

namespace SIGFUR.Wpf.Services;

public static class ShellService
{
    public static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    /// <summary>
    /// Abre o Explorador exatamente na pasta do arquivo e deixa o item selecionado.
    /// Quando o caminho já é uma pasta, abre a própria pasta.
    /// </summary>
    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            OpenPath(fullPath);
            return;
        }

        var folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        if (File.Exists(fullPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true
            });
            return;
        }

        OpenPath(folder);
    }

    public static void OpenCalculator()
    {
        Process.Start(new ProcessStartInfo { FileName = "calc.exe", UseShellExecute = true });
    }
}
