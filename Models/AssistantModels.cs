using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SIGFUR.Wpf.Models;

public sealed class AssistantSettings
{
    public string Model { get; set; } = "gpt-5.4-mini";
    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ReasoningEffort { get; set; } = "none";
    public int MaxOutputTokens { get; set; } = 2600;
    public int MaxHistoryMessages { get; set; } = 12;
    public int MaxAttachmentCharacters { get; set; } = 35_000;
    public bool EnableLocalDataTools { get; set; } = true;
    public bool EnableAttachments { get; set; } = true;
    public bool RedactSensitiveData { get; set; } = true;
    public bool SaveHistoryLocally { get; set; } = true;
    public bool ConfirmOperationalActions { get; set; } = true;
    public bool HardBudgetLimit { get; set; } = true;
    public decimal MonthlyBudgetBrl { get; set; } = 10m;
    public decimal DollarRate { get; set; } = 5.20m;
    public string OperatorInstructions { get; set; } = string.Empty;
    public string DiexRitex { get; set; } = string.Empty;
}

public sealed class AssistantConversationStore
{
    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<AssistantConversationMessage> Messages { get; set; } = [];
}

public sealed class AssistantConversationMessage
{
    public string Role { get; set; } = "assistant";
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<string> AttachmentNames { get; set; } = [];

    // Links seguros exibidos dentro do balão da conversa.
    // O assistente nunca executa a ação sozinho; o operador precisa clicar.
    public List<AssistantPendingAction> Actions { get; set; } = [];

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCostBrl { get; set; }
    public bool IsError { get; set; }

    public string RoleLabel => Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "VOCÊ" : "ASSISTENTE SIGFUR";
    public string TimeLabel => CreatedAt.ToString("dd/MM/yyyy HH:mm");
    public bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);
    public bool IsAssistant => !IsUser;
    public string CostLabel => EstimatedCostBrl <= 0 ? string.Empty : $"Estimado: {EstimatedCostBrl:C4}";
}

public sealed class AssistantAttachmentItem : INotifyPropertyChanged
{
    private string _path = string.Empty;
    private long _sizeBytes;
    private string _extractedText = string.Empty;
    private string _status = "Aguardando";
    public string Path { get => _path; set { _path = value ?? string.Empty; OnChanged(nameof(Path)); OnChanged(nameof(FileName)); OnChanged(nameof(Extension)); } }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).ToUpperInvariant();
    public long SizeBytes { get => _sizeBytes; set { _sizeBytes = value; OnChanged(nameof(SizeBytes)); OnChanged(nameof(SizeLabel)); } }
    public string SizeLabel => SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024d:0.0} KB" : $"{SizeBytes / 1024d / 1024d:0.0} MB";
    public string ExtractedText { get => _extractedText; set { _extractedText = value ?? string.Empty; OnChanged(nameof(ExtractedText)); OnChanged(nameof(IsReady)); } }
    public string Status { get => _status; set { _status = value ?? string.Empty; OnChanged(nameof(Status)); OnChanged(nameof(IsReady)); OnChanged(nameof(HasError)); } }
    public bool IsReady => Status.StartsWith("Pronto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ExtractedText);
    public bool HasError => Status.StartsWith("Erro", StringComparison.OrdinalIgnoreCase);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AssistantUsageRecord
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Model { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public decimal EstimatedCostBrl { get; set; }
}

public sealed class AssistantUsageStore
{
    public List<AssistantUsageRecord> Records { get; set; } = [];
}

public sealed class AssistantUsageSummary
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int Requests { get; set; }
    public decimal EstimatedCostBrl { get; set; }
    public decimal BudgetBrl { get; set; }
    public decimal RemainingBrl => Math.Max(0, BudgetBrl - EstimatedCostBrl);
    public double Percent => BudgetBrl <= 0 ? 0 : Math.Min(100, (double)(EstimatedCostBrl / BudgetBrl * 100));
    public string Display => $"{EstimatedCostBrl:C2} de {BudgetBrl:C2}";
}

public sealed class AssistantToolExecutionResult
{
    public string OutputJson { get; set; } = "{}";
    public List<AssistantPendingAction> PendingActions { get; set; } = [];
    public string Summary { get; set; } = string.Empty;
}

public sealed class AssistantPendingAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> FilePaths { get; set; } = [];
    public int Copies { get; set; } = 1;
    public bool RequiresConfirmation { get; set; }
    public string Icon { get; set; } = string.Empty;
    public Dictionary<string, string> Payload { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string ConversationLinkLabel
    {
        get
        {
            if (Payload.TryGetValue("display", out var display) && !string.IsNullOrWhiteSpace(display))
                return display.Trim();

            var label = string.IsNullOrWhiteSpace(Title) ? Description : Title;
            label = CleanActionLabel(label);
            if (!string.IsNullOrWhiteSpace(label)) return label;

            var path = FilePaths.FirstOrDefault();
            return string.IsNullOrWhiteSpace(path) ? "Abrir item vinculado" : Path.GetFileName(path);
        }
    }

    [JsonIgnore]
    public string ConversationToolTip
    {
        get
        {
            var path = FilePaths.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(Description) && !Description.Equals(Title, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(path) ? Description : Description + Environment.NewLine + path;
            return string.IsNullOrWhiteSpace(path) ? "Abrir somente se necessário." : path;
        }
    }

    [JsonIgnore]
    public bool IsConversationLink => Type.Equals("open_file", StringComparison.OrdinalIgnoreCase)
                                      || Type.Equals("reveal_file", StringComparison.OrdinalIgnoreCase)
                                      || Type.Equals("open_folder", StringComparison.OrdinalIgnoreCase)
                                      || Type.Equals("open_wallet", StringComparison.OrdinalIgnoreCase)
                                      || Type.Equals("open_url", StringComparison.OrdinalIgnoreCase)
                                      || Type.Equals("print", StringComparison.OrdinalIgnoreCase)
                                      || Type.Equals("copy_text", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasFilePath => FilePaths.Any(path => !string.IsNullOrWhiteSpace(path));

    [JsonIgnore]
    public string FirstFilePath => FilePaths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty;

    [JsonIgnore]
    public string PrimaryButtonLabel => Type.Equals("copy_text", StringComparison.OrdinalIgnoreCase)
        ? "Copiar"
        : Type.Equals("print", StringComparison.OrdinalIgnoreCase) ? "Preparar" : "Abrir se necessário";

    private static string CleanActionLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Trim();
        var prefixes = new[] { "Abrir ", "Mostrar PDF ", "Mostrar pasta ", "Mostrar ", "Imprimir ", "Preparar impressão", "Preparar " };
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }
        return string.IsNullOrWhiteSpace(text) ? value.Trim() : text;
    }
}

public sealed class AssistantResponse
{
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<AssistantPendingAction> SuggestedActions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<AssistantSearchResult> Results { get; set; } = [];

    [JsonIgnore]
    public bool HasResults => Results.Count > 0;
}

public sealed class AssistantSearchResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string MilitaryName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string BulletinNumber { get; set; } = string.Empty;
    public DateTime? BulletinDate { get; set; }
    public int Year { get; set; }
    public int? Page { get; set; }
    public string Source { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string SearchTerm { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public double Score { get; set; }
    public int? PersonId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public DateTime? Date { get; set; }
    public string Competence { get; set; } = string.Empty;
    public List<AssistantPendingAction> Actions { get; set; } = [];

    [JsonIgnore] public string EffectiveKind => string.IsNullOrWhiteSpace(Kind) ? Type : Kind;
    [JsonIgnore] public string EffectiveSource => string.IsNullOrWhiteSpace(Source) ? Module : Source;
    [JsonIgnore] public string ConfidenceLabel => Confidence <= 0 ? string.Empty : $"{Confidence:0}%";
}

public sealed class AssistantApiResult
{
    public string Text { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCostBrl { get; set; }
    public List<string> ToolSummaries { get; set; } = [];
    public List<AssistantPendingAction> PendingActions { get; set; } = [];
}

public sealed class AssistantMessageView
{
    public AssistantConversationMessage Message { get; set; } = new();
    public string RoleLabel => Message.RoleLabel;
    public string Content => Message.Content;
    public string TimeLabel => Message.TimeLabel;
    public string CostLabel => Message.CostLabel;
    public bool IsUser => Message.IsUser;
    public bool IsError => Message.IsError;
    public string AttachmentLabel => Message.AttachmentNames.Count == 0 ? string.Empty : string.Join(" • ", Message.AttachmentNames);

    [JsonIgnore]
    public IReadOnlyList<AssistantPendingAction> ActionLinks => Message.Actions
        .Where(x => x.IsConversationLink)
        .GroupBy(x => string.Join("|", x.Type, x.ConversationLinkLabel, string.Join(";", x.FilePaths), string.Join(";", x.Payload.Select(kv => kv.Key + "=" + kv.Value))))
        .Select(x => x.First())
        .ToList();
}
