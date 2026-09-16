using System.Text.Json.Nodes;
using SIGFUR.Wpf.Models;

namespace SIGFUR.Wpf.Services;

public static class AssistantStructuredSchemas
{
    public static AssistantJsonSchema BulletinRescue { get; } = new("sigfur_bulletin_rescue_v2", Parse("""
    {"type":"object","additionalProperties":false,"properties":{"items":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
      "source_file":{"type":"string"},"bulletin_number":{"type":"string"},"bulletin_date":{"type":"string"},"page":{"type":"integer","minimum":1},
      "source_excerpt":{"type":"string"},"military_name":{"type":"string"},"cpf":{"type":"string"},"prec_cp":{"type":"string"},
      "subject":{"type":"string"},"financial_effect":{"type":"string"},"reference_period":{"type":"string"},"expected_rubric_or_code":{"type":"string"},
      "expected_value":{"type":["number","null"]},"confidence":{"type":"number","minimum":0,"maximum":1},"reasoning_summary":{"type":"string"},"requires_human_review":{"type":"boolean"}},
      "required":["source_file","bulletin_number","bulletin_date","page","source_excerpt","military_name","cpf","prec_cp","subject","financial_effect","reference_period","expected_rubric_or_code","expected_value","confidence","reasoning_summary","requires_human_review"]}}},"required":["items"]}
    """));

    public static AssistantJsonSchema PaymentAudit { get; } = new("sigfur_payment_monthly_v2", Parse("""
    {"type":"object","additionalProperties":false,"properties":{"results":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
      "item_id":{"type":"string"},"status":{"type":"string","enum":["COERENTE","DIVERGENCIA","SEM_REFLEXO_NO_CONTRACHEQUE","SEM_PUBLICACAO_ENCONTRADA","REVISÃO_NECESSÁRIA","INCONCLUSIVO"]},
      "military":{"type":"string"},"finding":{"type":"string"},"bulletin_evidence":{"type":"string"},"paystub_evidence":{"type":"string"},
      "difference":{"type":["number","null"]},"recommended_action":{"type":"string"},"confidence":{"type":"number","minimum":0,"maximum":1},"evidence_gap":{"type":"string"}},
      "required":["item_id","status","military","finding","bulletin_evidence","paystub_evidence","difference","recommended_action","confidence","evidence_gap"]}}},"required":["results"]}
    """));

    public static AssistantJsonSchema BulletinAudit { get; } = new("sigfur_bulletin_audit_v2", Parse("""
    {"type":"object","additionalProperties":false,"properties":{
      "overall_status":{"type":"string","enum":["COERENTE","ERRO_OBJETIVO","ALERTA","AUSÊNCIA_DE_EVIDÊNCIA","INCONCLUSIVO"]},
      "findings":{"type":"array","items":{"type":"object","additionalProperties":false,"properties":{
        "category":{"type":"string","enum":["ERRO_OBJETIVO","ALERTA","AUSÊNCIA_DE_EVIDÊNCIA","SUGESTÃO","INCONCLUSIVO"]},
        "finding":{"type":"string"},"source_excerpt":{"type":"string"},"local_evidence":{"type":"string"},"recommended_action":{"type":"string"}},
        "required":["category","finding","source_excerpt","local_evidence","recommended_action"]}},
      "evidence_gaps":{"type":"array","items":{"type":"string"}},"human_checklist":{"type":"array","items":{"type":"string"}}},
      "required":["overall_status","findings","evidence_gaps","human_checklist"]}
    """));

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
