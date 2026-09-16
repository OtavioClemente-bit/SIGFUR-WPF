using System.Globalization;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class AssistantActionRegistry
{
    public static AssistantPendingAction OpenFile(string title, string path, string description = "") => new()
    {
        Type = "open_file",
        Title = string.IsNullOrWhiteSpace(title) ? "Abrir arquivo" : title,
        Description = string.IsNullOrWhiteSpace(description) ? path : description,
        FilePaths = string.IsNullOrWhiteSpace(path) ? [] : [path],
        Icon = "📄",
        RequiresConfirmation = true
    };

    public static AssistantPendingAction RevealFile(string title, string path, string description = "") => new()
    {
        Type = "reveal_file",
        Title = string.IsNullOrWhiteSpace(title) ? "Mostrar na pasta" : title,
        Description = string.IsNullOrWhiteSpace(description) ? path : description,
        FilePaths = string.IsNullOrWhiteSpace(path) ? [] : [path],
        Icon = "📁",
        RequiresConfirmation = true
    };

    public static AssistantPendingAction OpenFolder(string title, string path, string description = "") => new()
    {
        Type = "open_folder",
        Title = string.IsNullOrWhiteSpace(title) ? "Abrir pasta" : title,
        Description = string.IsNullOrWhiteSpace(description) ? path : description,
        FilePaths = string.IsNullOrWhiteSpace(path) ? [] : [path],
        Icon = "📂",
        RequiresConfirmation = true
    };

    public static AssistantPendingAction PrintFile(string title, string path, int copies = 1, string description = "") => new()
    {
        Type = "print",
        Title = string.IsNullOrWhiteSpace(title) ? "Imprimir arquivo" : title,
        Description = string.IsNullOrWhiteSpace(description) ? path : description,
        FilePaths = string.IsNullOrWhiteSpace(path) ? [] : [path],
        Copies = Math.Max(1, copies),
        Icon = "🖨",
        RequiresConfirmation = true
    };

    public static AssistantPendingAction OpenWallet(MilitaryRecord military) => new()
    {
        Type = "open_wallet",
        Title = "Abrir carteira",
        Description = $"Abrir a carteira do militar {military.ShortRank} {military.Name}.",
        Icon = "🪪",
        RequiresConfirmation = true,
        Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["military_id"] = military.Id.ToString(CultureInfo.InvariantCulture) }
    };

    public static AssistantPendingAction OpenRoute(MilitaryRecord military, string url) => new()
    {
        Type = "open_url",
        Title = "Abrir rota",
        Description = $"Abrir rota do endereço salvo de {military.ShortRank} {military.Name} até a OM.",
        Icon = "🗺",
        RequiresConfirmation = true,
        Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["url"] = url }
    };

    public static AssistantPendingAction CopyText(string title, string text) => new()
    {
        Type = "copy_text",
        Title = string.IsNullOrWhiteSpace(title) ? "Copiar texto" : title,
        Description = "Copiar o resumo para a área de transferência.",
        Icon = "📋",
        RequiresConfirmation = false,
        Payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["text"] = text ?? string.Empty }
    };
}
