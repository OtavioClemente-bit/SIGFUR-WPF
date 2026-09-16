using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SIGFUR.Wpf.Models;

public sealed class AssistantStructuredResult<T>
{
    public required T Value { get; init; }
    public int InputTokens { get; init; }
    public int CachedInputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCostBrl { get; init; }
    public bool RetriedInvalidOutput { get; init; }
}

public sealed class AiRescuePublication
{
    [JsonPropertyName("source_file")] public string SourceFile { get; set; } = string.Empty;
    [JsonPropertyName("bulletin_number")] public string BulletinNumber { get; set; } = string.Empty;
    [JsonPropertyName("bulletin_date")] public string BulletinDate { get; set; } = string.Empty;
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("source_excerpt")] public string SourceExcerpt { get; set; } = string.Empty;
    [JsonPropertyName("military_name")] public string MilitaryName { get; set; } = string.Empty;
    [JsonPropertyName("cpf")] public string Cpf { get; set; } = string.Empty;
    [JsonPropertyName("prec_cp")] public string PrecCp { get; set; } = string.Empty;
    [JsonPropertyName("subject")] public string Subject { get; set; } = string.Empty;
    [JsonPropertyName("financial_effect")] public string FinancialEffect { get; set; } = string.Empty;
    [JsonPropertyName("reference_period")] public string ReferencePeriod { get; set; } = string.Empty;
    [JsonPropertyName("expected_rubric_or_code")] public string ExpectedRubricOrCode { get; set; } = string.Empty;
    [JsonPropertyName("expected_value")] public decimal? ExpectedValue { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("reasoning_summary")] public string ReasoningSummary { get; set; } = string.Empty;
    [JsonPropertyName("requires_human_review")] public bool RequiresHumanReview { get; set; } = true;
}

public sealed class AiRescueResponse
{
    [JsonPropertyName("items")] public List<AiRescuePublication> Items { get; set; } = [];
}

public sealed class PaymentAiFinding
{
    [JsonPropertyName("item_id")] public string ItemId { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = "INCONCLUSIVO";
    [JsonPropertyName("military")] public string Military { get; set; } = string.Empty;
    [JsonPropertyName("finding")] public string Finding { get; set; } = string.Empty;
    [JsonPropertyName("bulletin_evidence")] public string BulletinEvidence { get; set; } = string.Empty;
    [JsonPropertyName("paystub_evidence")] public string PaystubEvidence { get; set; } = string.Empty;
    [JsonPropertyName("difference")] public decimal? Difference { get; set; }
    [JsonPropertyName("recommended_action")] public string RecommendedAction { get; set; } = string.Empty;
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("evidence_gap")] public string EvidenceGap { get; set; } = string.Empty;
}

public sealed class PaymentAiAuditResponse
{
    [JsonPropertyName("results")] public List<PaymentAiFinding> Results { get; set; } = [];
}

public sealed class BulletinAiAuditFinding
{
    [JsonPropertyName("category")] public string Category { get; set; } = "INCONCLUSIVO";
    [JsonPropertyName("finding")] public string Finding { get; set; } = string.Empty;
    [JsonPropertyName("source_excerpt")] public string SourceExcerpt { get; set; } = string.Empty;
    [JsonPropertyName("local_evidence")] public string LocalEvidence { get; set; } = string.Empty;
    [JsonPropertyName("recommended_action")] public string RecommendedAction { get; set; } = string.Empty;
}

public sealed class BulletinAiAuditResponse
{
    [JsonPropertyName("overall_status")] public string OverallStatus { get; set; } = "INCONCLUSIVO";
    [JsonPropertyName("findings")] public List<BulletinAiAuditFinding> Findings { get; set; } = [];
    [JsonPropertyName("evidence_gaps")] public List<string> EvidenceGaps { get; set; } = [];
    [JsonPropertyName("human_checklist")] public List<string> HumanChecklist { get; set; } = [];
}

public sealed record AssistantJsonSchema(string Name, JsonObject Schema, bool Strict = true);
