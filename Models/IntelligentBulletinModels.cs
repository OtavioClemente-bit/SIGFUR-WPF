using System.Text.Json.Serialization;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class IntelligentBulletinStore
{
    [JsonPropertyName("version")] public int Version { get; set; } = 12;
    [JsonPropertyName("items")] public List<IntelligentBulletinFile> Items { get; set; } = [];
}

public sealed class IntelligentBulletinFile
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("hash_sha256")] public string HashSha256 { get; set; } = string.Empty;
    [JsonPropertyName("numero_bi")] public string BulletinNumber { get; set; } = "—";
    [JsonPropertyName("data_bi")] public string BulletinDate { get; set; } = "—";
    [JsonPropertyName("data_iso")] public string DateIso { get; set; } = string.Empty;
    [JsonPropertyName("periodo")] public string Period { get; set; } = string.Empty;
    [JsonPropertyName("pasta_label")] public string SourceFolderLabel { get; set; } = string.Empty;
    [JsonPropertyName("pasta_origem")] public string SourceFolder { get; set; } = string.Empty;
    [JsonPropertyName("nome_arquivo_original")] public string OriginalFileName { get; set; } = string.Empty;
    [JsonPropertyName("nome_arquivo")] public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("caminho_pdf")] public string PdfPath { get; set; } = string.Empty;
    [JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPath { get => null; set { if (string.IsNullOrWhiteSpace(PdfPath) && !string.IsNullOrWhiteSpace(value)) PdfPath = value; } }
    [JsonPropertyName("arquivo_pdf"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPdfPath { get => null; set { if (string.IsNullOrWhiteSpace(PdfPath) && !string.IsNullOrWhiteSpace(value)) PdfPath = value; } }
    [JsonPropertyName("texto_path")] public string TextCachePath { get; set; } = string.Empty;
    [JsonPropertyName("parse_path")] public string ParseCachePath { get; set; } = string.Empty;
    [JsonPropertyName("tamanho_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("salvo_em")] public string SavedAt { get; set; } = string.Empty;
    [JsonPropertyName("paginas")] public int Pages { get; set; }
    [JsonPropertyName("tipo_boletim")] public string BulletinType { get; set; } = "BI";
    [JsonPropertyName("versao_parser")] public int ParserVersion { get; set; }
    [JsonPropertyName("mencoes")] public List<BulletinMentionItem> Mentions { get; set; } = [];
    [JsonPropertyName("achados")] public List<IntelligentBulletinFinding> Findings { get; set; } = [];

    [JsonIgnore] public string DisplayNumber => string.IsNullOrWhiteSpace(BulletinNumber) ? "—" : BulletinNumber;
    [JsonIgnore] public string DisplayDate => string.IsNullOrWhiteSpace(BulletinDate) ? "—" : BulletinDate;
    [JsonIgnore] public string DisplayPeriod => string.IsNullOrWhiteSpace(Period) ? "—" : Period;
    [JsonIgnore] public string SizeText => SizeBytes <= 0 ? "—" : SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024d:0.0} KB" : $"{SizeBytes / 1024d / 1024d:0.0} MB";
    [JsonIgnore] public int FindingCount => Findings.Count;
}

public sealed class IntelligentBulletinFinding
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("categoria")] public string Category { get; set; } = "Outros";
    [JsonPropertyName("tipo")] public string Type { get; set; } = "Menção";
    [JsonPropertyName("boletim")] public string Bulletin { get; set; } = "—";
    [JsonPropertyName("data_bi")] public string BulletinDate { get; set; } = "—";
    [JsonPropertyName("militar")] public string Military { get; set; } = string.Empty;
    [JsonPropertyName("nome_completo")] public string FullName { get; set; } = string.Empty;
    [JsonPropertyName("nome_guerra")] public string WarName { get; set; } = string.Empty;
    [JsonPropertyName("posto")] public string Rank { get; set; } = string.Empty;
    [JsonPropertyName("assunto_nota")] public string Subject { get; set; } = string.Empty;
    [JsonPropertyName("previa")] public string Preview { get; set; } = string.Empty;
    [JsonPropertyName("detalhe")] public string Detail { get; set; } = string.Empty;
    [JsonPropertyName("contexto")] public string Context { get; set; } = string.Empty;
    [JsonPropertyName("pagina")] public int Page { get; set; } = 1;
    [JsonPropertyName("arquivo")] public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("arquivo_pdf")] public string PdfPath { get; set; } = string.Empty;
    [JsonPropertyName("termo_pdf")] public string PdfSearchTerm { get; set; } = string.Empty;
    [JsonPropertyName("titulo_nota")] public string NoteTitle { get; set; } = string.Empty;
    [JsonPropertyName("texto_nota")] public string NoteText { get; set; } = string.Empty;
    [JsonPropertyName("cpf")] public string MentionedCpf { get; set; } = string.Empty;
    [JsonPropertyName("prec_cp")] public string MentionedPrecCp { get; set; } = string.Empty;
    [JsonPropertyName("militar_id")] public int? MilitaryId { get; set; }
    [JsonPropertyName("vinculado_banco")] public bool IsDatabaseMatch { get; set; }
    [JsonPropertyName("tem_consequencia")] public bool HasConsequence { get; set; }
    [JsonPropertyName("consequencia_furriel")] public bool IsFurrielConsequence { get; set; }
    [JsonPropertyName("texto_consequencia")] public string ConsequenceText { get; set; } = string.Empty;
    [JsonPropertyName("assunto_nota_exibicao")] public string SubjectNoteDisplay { get; set; } = string.Empty;
    [JsonIgnore] public bool Reviewed { get; set; }
    [JsonIgnore] public string ReviewedText => Reviewed ? "OK" : "PENDENTE";
    [JsonIgnore] public bool Reviewable => !Category.Equals("Serviço", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string DisplayMilitary => string.IsNullOrWhiteSpace(FullName) ? (string.IsNullOrWhiteSpace(Military) ? "—" : Military) : FullName;
    [JsonIgnore] public string DisplaySubject => !string.IsNullOrWhiteSpace(SubjectNoteDisplay) ? SubjectNoteDisplay : string.IsNullOrWhiteSpace(Subject) ? Type : Subject;
    [JsonIgnore] public string DisplayPreview => string.IsNullOrWhiteSpace(Preview) ? Detail : Preview;
    [JsonIgnore] public string ConsequenceDisplay => HasConsequence ? "Sim" : "Não";
}

public sealed class IntelligentBulletinReviewEntry
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("categoria")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("boletim")] public string Bulletin { get; set; } = string.Empty;
    [JsonPropertyName("arquivo")] public string File { get; set; } = string.Empty;
    [JsonPropertyName("militar")] public string Military { get; set; } = string.Empty;
    [JsonPropertyName("resumo")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("observacao")] public string Note { get; set; } = string.Empty;
    [JsonPropertyName("verificado_em")] public string ReviewedAt { get; set; } = string.Empty;
}

public sealed class IntelligentBulletinImportResult
{
    public int Imported { get; set; }
    public int Updated { get; set; }
    public int Duplicates { get; set; }
    public List<string> Errors { get; } = [];
    public int Total => Imported + Updated + Duplicates;
}

public sealed class IntelligentBulletinMilitaryOption
{
    public int Id { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public string Source { get; set; } = "Ativos";
    public string DisplayLabel => $"{MilitaryRankService.ShortName(Rank)} {FullName}{(Source == "Ativos" ? string.Empty : $" · {Source}")}".Trim();
    public string SearchText => string.Join(" ", FullName, WarName, Cpf, Identity, PrecCp, Rank, Source);
    public override string ToString() => DisplayLabel;
}

public sealed class IntelligentBulletinSettings
{
    public string PersonnelSource { get; set; } = "Ativos";
    public string PeriodMode { get; set; } = "Mês/Ano";
    public string Period { get; set; } = "Todos";
    public string Year { get; set; } = "Todos";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string LastFolder { get; set; } = string.Empty;
    public bool AutoUpdateFromLastFolder { get; set; }
    public string Search { get; set; } = string.Empty;
    public string SubjectFilter { get; set; } = "Todos";
    public string ConsequenceFilter { get; set; } = "Todos";
    public int SelectedTabIndex { get; set; } = 1;
    public string MilitarySearch { get; set; } = string.Empty;
    public string PersonIndexSearch { get; set; } = string.Empty;
    public string PersonIndexYear { get; set; } = "Todos";
    public string PersonIndexMonth { get; set; } = "Todos";
    public string PersonIndexSubject { get; set; } = "Todos";
    public string PersonIndexNote { get; set; } = "Todos";
    public string PersonIndexLinkFilter { get; set; } = "Todos";
    public string PersonIndexUser { get; set; } = "Todos";
    public string PersonIndexPerson { get; set; } = "Todos";
    public string PersonIndexSubjectSearch { get; set; } = string.Empty;
    public string PersonIndexSubjectNoteSearch { get; set; } = string.Empty;
    public DateTime? PersonIndexDownloadStartDate { get; set; }
    public DateTime? PersonIndexDownloadEndDate { get; set; }
}
