using System.Text.Json.Serialization;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class FurrielIndexStore
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 9;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<FurrielBulletinFile> Files { get; set; } = [];

    [JsonPropertyName("signed_files")]
    public Dictionary<string, FurrielSignedFileInfo> SignedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("indice_por_assunto")]
    public List<FurrielSubjectIndexEntry> SubjectIndex { get; set; } = [];
}

public sealed class FurrielSubjectIndexEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("source_pdf_path")] public string SourcePdfPath { get; set; } = string.Empty;
    [JsonPropertyName("source_pdf_hash")] public string SourcePdfHash { get; set; } = string.Empty;
    [JsonPropertyName("adt_numero")] public string BulletinNumber { get; set; } = string.Empty;
    [JsonPropertyName("data_adt")] public string BulletinDate { get; set; } = string.Empty;
    [JsonPropertyName("pagina")] public int Page { get; set; }
    [JsonPropertyName("nota_sisbol")] public string NoteNumber { get; set; } = string.Empty;
    [JsonPropertyName("usuario")] public string SisbolUser { get; set; } = string.Empty;
    [JsonPropertyName("assunto")] public string Subject { get; set; } = string.Empty;
    [JsonPropertyName("nota_tipo")] public string NoteType { get; set; } = string.Empty;
    [JsonPropertyName("assunto_nota")] public string SubjectNoteDisplay { get; set; } = string.Empty;
    [JsonPropertyName("texto_busca_normalizado")] public string SearchTextNormalized { get; set; } = string.Empty;
}

public sealed class FurrielBulletinFile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("original_name")]
    public string OriginalName { get; set; } = string.Empty;

    [JsonPropertyName("stored_path")]
    public string StoredPath { get; set; } = string.Empty;

    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("source_dir")]
    public string SourceDirectory { get; set; } = string.Empty;

    [JsonPropertyName("source_original_name")]
    public string SourceOriginalName { get; set; } = string.Empty;

    [JsonPropertyName("boletim")]
    public string Bulletin { get; set; } = "—";

    [JsonPropertyName("bar")]
    public string Bar { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string Date { get; set; } = "—";

    [JsonPropertyName("titulo")]
    public string Title { get; set; } = "Aditamento do Furriel";

    [JsonPropertyName("pages")]
    public int Pages { get; set; }

    [JsonPropertyName("line_count")]
    public int LineCount { get; set; }

    [JsonPropertyName("indexed_at")]
    public string IndexedAt { get; set; } = string.Empty;

    [JsonPropertyName("lines")]
    public List<FurrielIndexedLine> Lines { get; set; } = [];

    [JsonPropertyName("mentions")]
    public List<BulletinMentionItem> Mentions { get; set; } = [];
    [JsonPropertyName("versao_parser")] public int ParserVersion { get; set; }

    [JsonIgnore]
    public string SignedStatus { get; set; } = "NÃO";

    [JsonIgnore]
    public string DisplayBulletin => string.IsNullOrWhiteSpace(Bulletin) ? "—" : Bulletin;

    [JsonIgnore]
    public string DisplayDate => string.IsNullOrWhiteSpace(Date) ? "—" : Date;

    [JsonIgnore]
    public string DisplayBar => string.IsNullOrWhiteSpace(Bar) ? "—" : Bar;
}

public sealed class FurrielIndexedLine
{
    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("norm")]
    public string Normalized { get; set; } = string.Empty;

    [JsonPropertyName("digits")]
    public string Digits { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "—";

    [JsonPropertyName("major")]
    public string Major { get; set; } = "—";
}

public sealed class FurrielSignedFileInfo
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("original_name")]
    public string OriginalName { get; set; } = string.Empty;

    [JsonPropertyName("boletim")]
    public string Bulletin { get; set; } = "—";

    [JsonPropertyName("bar")]
    public string Bar { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string Date { get; set; } = "—";

    [JsonPropertyName("saved_at")]
    public string SavedAt { get; set; } = string.Empty;

    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("source_dir")]
    public string SourceDirectory { get; set; } = string.Empty;

    [JsonPropertyName("source_original_name")]
    public string SourceOriginalName { get; set; } = string.Empty;
}

public sealed class FurrielMilitaryOption
{
    public int Id { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public string Source { get; set; } = "Ativos";
    public string Situation { get; set; } = string.Empty;
    public string DisplayLabel => $"{MilitaryRankService.ShortName(Rank)} {FullName}{(Source.Equals("Ativos", StringComparison.OrdinalIgnoreCase) ? string.Empty : $" · {Source}")}".Trim();
    public override string ToString() => DisplayLabel;
}

public sealed class FurrielSearchResult
{
    public string Military { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string PdfSearchTerm { get; set; } = string.Empty;
    public bool MatchFromDatabase { get; set; }
    public string Type { get; set; } = "Menção";
    public string Bulletin { get; set; } = string.Empty;
    public string Bar { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public int Page { get; set; }
    public string Signed { get; set; } = "NÃO";
    public string Subject { get; set; } = string.Empty;
    public string Preview { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string PdfPath { get; set; } = string.Empty;
    public string SignedPdfPath { get; set; } = string.Empty;
    public string SignedFileName { get; set; } = string.Empty;
    public string IndexedAt { get; set; } = string.Empty;
    public string SubjectNoteDisplay { get; set; } = string.Empty;
    public string NoteText { get; set; } = string.Empty;
    public bool HasConsequence { get; set; }
    public bool IsFurrielConsequence { get; set; }
    public string ConsequenceText { get; set; } = string.Empty;
    public string ConsequenceDisplay => HasConsequence ? "Sim" : "Não";
}

public sealed class FurrielPeriodFilter
{
    public string Month { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
}

public sealed class FurrielImportSummary
{
    public int CommonNew { get; set; }
    public int CommonUpdated { get; set; }
    public int SignedNew { get; set; }
    public int SignedUpdated { get; set; }
    public List<string> Errors { get; } = [];
    public int TotalProcessed => CommonNew + CommonUpdated + SignedNew + SignedUpdated;
}

public sealed class FurrielModuleSettings
{
    [JsonPropertyName("fonte_pessoal_boletim_furriel")]
    public string PersonnelSource { get; set; } = "Ativos";

    [JsonPropertyName("busca_texto_sem_cadastro_boletim_furriel")]
    public bool SearchTextWhenNotRegistered { get; set; } = true;

    [JsonPropertyName("modo_periodo_boletim_furriel")]
    public string PeriodMode { get; set; } = "Mês/Ano";

    [JsonPropertyName("mes_boletim_furriel")]
    public int Month { get; set; }

    [JsonPropertyName("ano_boletim_furriel")]
    public string Year { get; set; } = "Todos";

    [JsonPropertyName("data_inicial_boletim_furriel")]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("data_final_boletim_furriel")]
    public DateTime? EndDate { get; set; }

    [JsonPropertyName("ultima_pasta_boletim_furriel")]
    public string LastFolder { get; set; } = string.Empty;

    [JsonPropertyName("atualizar_pasta_automaticamente_boletim_furriel")]
    public bool AutoUpdateFromLastFolder { get; set; }

    [JsonPropertyName("filtro_consequencia_boletim_furriel")]
    public string ConsequenceFilter { get; set; } = "Todos";
}
