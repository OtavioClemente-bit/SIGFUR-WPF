using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Windows;

namespace SIGFUR.Wpf.Services;

public static class BulletinIndividualExportService
{
    public static async Task<string?> ExportAsync(
        Window owner,
        string sourcePath,
        string suggestedFileName,
        string dialogTitle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            SigfurDialog.Show(owner, "O PDF selecionado não está disponível.", dialogTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var dialog = new SaveFileDialog
        {
            Title = dialogTitle,
            Filter = "Documento PDF (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SafeFileName(suggestedFileName)
        };
        if (dialog.ShowDialog(owner) != true) return null;

        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var destination = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None,
                         1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        if (SigfurDialog.Show(owner,
                $"Boletim individual exportado com sucesso.\n\n{dialog.FileName}\n\nDeseja abrir o PDF agora?",
                dialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            ShellService.OpenPath(dialog.FileName);

        return dialog.FileName;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string((value ?? string.Empty)
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(clean)) clean = "Boletim.pdf";
        if (!Path.GetExtension(clean).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) clean += ".pdf";
        return clean;
    }
}
