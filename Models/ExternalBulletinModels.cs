using System.Text.Json.Serialization;

namespace SIGFUR.Wpf.Models;

public static class ExternalBulletinKinds
{
    public const string Region = "REGIAO";
    public const string Cml = "CML";

    public static string DisplayName(string? value)
        => string.Equals(value, Cml, StringComparison.OrdinalIgnoreCase)
            ? "Aditamento CML"
            : "Boletim Regional";
}

public sealed class ExternalBulletinStore
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("items")] public List<ExternalBulletinFile> Items { get; set; } = [];
}

public sealed class ExternalBulletinFile
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("hash_sha256")] public string HashSha256 { get; set; } = string.Empty;
    [JsonPropertyName("origem")] public string Kind { get; set; } = ExternalBulletinKinds.Region;
    [JsonPropertyName("tipo_boletim")] public string BulletinType { get; set; } = string.Empty;
    [JsonPropertyName("numero")] public string BulletinNumber { get; set; } = "—";
    [JsonPropertyName("data")] public string BulletinDate { get; set; } = "—";
    [JsonPropertyName("data_iso")] public string DateIso { get; set; } = string.Empty;
    [JsonPropertyName("arquivo_original")] public string OriginalFileName { get; set; } = string.Empty;
    [JsonPropertyName("pasta_origem")] public string SourceFolder { get; set; } = string.Empty;
    [JsonPropertyName("arquivo_salvo")] public string StoredPath { get; set; } = string.Empty;
    [JsonPropertyName("paginas")] public int Pages { get; set; }
    [JsonPropertyName("tamanho_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("importado_em")] public string ImportedAt { get; set; } = string.Empty;
    [JsonPropertyName("termo_indexado")] public string IndexedSearchTerm { get; set; } = string.Empty;
    [JsonPropertyName("mencoes_ignoradas_primeira_parte")] public int IgnoredFirstPartMentions { get; set; }
    [JsonPropertyName("mencoes")] public List<ExternalBulletinMention> Mentions { get; set; } = [];

    [JsonIgnore] public string KindDisplay => ExternalBulletinKinds.DisplayName(Kind);
    [JsonIgnore] public string DisplayNumber => string.IsNullOrWhiteSpace(BulletinNumber) ? "—" : BulletinNumber;
    [JsonIgnore] public string DisplayDate => string.IsNullOrWhiteSpace(BulletinDate) ? "—" : BulletinDate;
    [JsonIgnore] public int MentionCount => Mentions?.Count ?? 0;
    [JsonIgnore] public string SizeText => SizeBytes <= 0 ? "—" : SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024d:0.0} KB" : $"{SizeBytes / 1024d / 1024d:0.0} MB";
    [JsonIgnore] public string IgnoredText => Kind == ExternalBulletinKinds.Region && IgnoredFirstPartMentions > 0
        ? $"{IgnoredFirstPartMentions} menção(ões) da 1ª Parte ignorada(s)"
        : "—";
}

public sealed class ExternalBulletinMention
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("arquivo_id")] public string FileId { get; set; } = string.Empty;
    [JsonPropertyName("origem")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("boletim")] public string Bulletin { get; set; } = string.Empty;
    [JsonPropertyName("data")] public string BulletinDate { get; set; } = string.Empty;
    [JsonPropertyName("pagina")] public int Page { get; set; } = 1;
    [JsonPropertyName("ocorrencia_documento")] public int DocumentOccurrence { get; set; } = 1;
    [JsonPropertyName("termo_pdf")] public string PdfSearchTerm { get; set; } = string.Empty;
    [JsonPropertyName("secao")] public string Section { get; set; } = string.Empty;
    [JsonPropertyName("tipo")] public string Type { get; set; } = "Menção";
    [JsonPropertyName("resumo")] public string Summary { get; set; } = string.Empty;
    [JsonPropertyName("linha")] public string MatchLine { get; set; } = string.Empty;
    [JsonPropertyName("contexto")] public string Context { get; set; } = string.Empty;
    [JsonPropertyName("valor")] public string Amount { get; set; } = string.Empty;
    [JsonPropertyName("evento")] public string Event { get; set; } = string.Empty;
    [JsonPropertyName("duracao")] public string Duration { get; set; } = string.Empty;
    [JsonPropertyName("efetivo")] public string Personnel { get; set; } = string.Empty;
    [JsonPropertyName("arquivo_pdf")] public string PdfPath { get; set; } = string.Empty;

    [JsonIgnore] public string PageText => $"Pág. {Page}";
    [JsonIgnore] public string KindDisplay => ExternalBulletinKinds.DisplayName(Kind);
    [JsonIgnore] public string DetailText => string.Join("\n", new[]
    {
        $"{KindDisplay} {Bulletin} · {BulletinDate} · página {Page}",
        string.IsNullOrWhiteSpace(Section) ? string.Empty : $"Seção: {Section}",
        string.IsNullOrWhiteSpace(Type) ? string.Empty : $"Classificação: {Type}",
        string.IsNullOrWhiteSpace(Event) ? string.Empty : $"Evento: {Event}",
        string.IsNullOrWhiteSpace(Duration) ? string.Empty : $"Duração: {Duration}",
        string.IsNullOrWhiteSpace(Personnel) ? string.Empty : $"Efetivo: {Personnel}",
        string.IsNullOrWhiteSpace(Amount) ? string.Empty : $"Valor: {Amount}",
        string.Empty,
        Context
    }.Where(x => x is not null));
}

public sealed class ExternalBulletinSettings
{
    [JsonPropertyName("termo_pesquisa")] public string SearchTerm { get; set; } = "4ª Cia PE";
    [JsonPropertyName("ultima_pasta_regiao")] public string LastRegionFolder { get; set; } = string.Empty;
    [JsonPropertyName("ultima_pasta_cml")] public string LastCmlFolder { get; set; } = string.Empty;
    [JsonPropertyName("filtro_regiao")] public string RegionFilter { get; set; } = string.Empty;
    [JsonPropertyName("filtro_cml")] public string CmlFilter { get; set; } = string.Empty;
}

public sealed class ExternalBulletinImportResult
{
    public int Imported { get; set; }
    public int Updated { get; set; }
    public int Duplicates { get; set; }
    public int WithoutMention { get; set; }
    public List<string> Errors { get; } = [];
}
