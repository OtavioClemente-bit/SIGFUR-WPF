using System.Text.Json.Serialization;

namespace SIGFUR.Wpf.Models;

public sealed class BulletinMentionItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("tipo_boletim")] public string BulletinType { get; set; } = string.Empty;
    [JsonPropertyName("numero_boletim")] public string BulletinNumber { get; set; } = string.Empty;
    [JsonPropertyName("data_boletim")] public DateTime? BulletinDate { get; set; }
    [JsonPropertyName("assunto")] public string Subject { get; set; } = string.Empty;
    [JsonPropertyName("titulo_nota")] public string NoteTitle { get; set; } = string.Empty;
    [JsonPropertyName("assunto_nota")] public string SubjectNoteDisplay { get; set; } = string.Empty;
    [JsonPropertyName("texto_nota")] public string NoteText { get; set; } = string.Empty;
    [JsonPropertyName("trecho_nota")] public string NoteExcerpt { get; set; } = string.Empty;
    [JsonPropertyName("militar_id")] public int? MilitaryId { get; set; }
    [JsonPropertyName("nome_militar")] public string MentionedMilitaryName { get; set; } = string.Empty;
    [JsonPropertyName("nome_guerra")] public string MentionedMilitaryWarName { get; set; } = string.Empty;
    [JsonPropertyName("posto_graduacao")] public string MentionedMilitaryRank { get; set; } = string.Empty;
    [JsonPropertyName("cpf")] public string MentionedMilitaryCpf { get; set; } = string.Empty;
    [JsonPropertyName("prec_cp")] public string MentionedMilitaryPrecCp { get; set; } = string.Empty;
    [JsonPropertyName("vinculado_banco")] public bool IsDatabaseMatch { get; set; }
    [JsonPropertyName("mencao_na_consequencia")] public bool IsConsequenceMention { get; set; }
    [JsonPropertyName("tem_consequencia")] public bool HasConsequence { get; set; }
    [JsonPropertyName("consequencia_furriel")] public bool IsFurrielConsequence { get; set; }
    [JsonPropertyName("texto_consequencia")] public string ConsequenceText { get; set; } = string.Empty;
    [JsonPropertyName("arquivo_pdf")] public string SourceFilePath { get; set; } = string.Empty;
    [JsonPropertyName("pagina")] public int? PageNumber { get; set; }
    [JsonPropertyName("criado_em")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("atualizado_em")] public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [JsonIgnore] public string DisplayMilitary => string.IsNullOrWhiteSpace(MentionedMilitaryName) ? "Militar não identificado" : MentionedMilitaryName;
    [JsonIgnore] public string ConsequenceDisplay => HasConsequence ? "Sim" : "Não";
    [JsonIgnore] public string PageDisplay => PageNumber?.ToString() ?? "—";
    [JsonIgnore] public string SourceFileName => Path.GetFileName(SourceFilePath);
}

public sealed class BulletinMilitaryIdentity
{
    public int? Id { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
}
