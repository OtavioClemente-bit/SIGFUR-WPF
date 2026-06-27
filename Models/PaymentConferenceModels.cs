using System.Text.Json.Serialization;

namespace SIGFUR.Wpf.Models;

public sealed class PaymentConferenceBulletinFile
{
    public bool Selected { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Bulletin { get; set; } = "—";
    public string Bar { get; set; } = string.Empty;
    public string Date { get; set; } = "—";
    public string OriginalName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Source { get; set; } = "Boletim Furriel";
    public int Pages { get; set; }
    public int ExpectedItems { get; set; }
    public string Status { get; set; } = "Pendente";
    public string Display => $"Adt Furr {Bulletin} · {Date}".Trim(' ', '·');
    public bool Exists => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);
}

public sealed class PaymentConferenceExpectedItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Bulletin { get; set; } = "—";
    public string BulletinDate { get; set; } = "—";
    public string BulletinPath { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int DocumentOccurrence { get; set; } = 1;
    public string SectionTitle { get; set; } = string.Empty;
    public string PaymentType { get; set; } = "Pagamento";
    public string PaymentMode { get; set; } = "Conferir";
    public string ExpectedRubricPrefix { get; set; } = string.Empty;
    public string ExpectedRubricRule { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public double ExpectedAmount { get; set; }
    public string Context { get; set; } = string.Empty;
    public string MatchedMilitaryName { get; set; } = string.Empty;
    public int? MatchedMilitaryId { get; set; }
    public string MatchStatus { get; set; } = "Não conferido";
    public string ExpectedAmountText => ExpectedAmount > 0 ? MilitaryFormatting.FormatMoney(ExpectedAmount) : "—";
    public string PageText => $"Pág. {Page}";
    public string IdentityText => string.Join(" · ", new[] { Rank, Name }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed class PaymentConferenceResultRow
{
    public string Status { get; set; } = "Pendente";
    public string Severity { get; set; } = "info";
    public string Bulletin { get; set; } = "—";
    public string BulletinDate { get; set; } = "—";
    public string BulletinPath { get; set; } = string.Empty;
    public int BulletinPage { get; set; } = 1;
    public int DocumentOccurrence { get; set; } = 1;
    public string SectionTitle { get; set; } = string.Empty;
    public string PaymentType { get; set; } = string.Empty;
    public string PaymentMode { get; set; } = string.Empty;
    public string ExpectedRubricPrefix { get; set; } = string.Empty;
    public string Military { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public int? MilitaryId { get; set; }
    public string PaystubPath { get; set; } = string.Empty;
    public string PaystubFile => string.IsNullOrWhiteSpace(PaystubPath) ? "—" : System.IO.Path.GetFileName(PaystubPath);
    public double ExpectedAmount { get; set; }
    public double PaidAmount { get; set; }
    public double Difference => PaidAmount - ExpectedAmount;
    public string ExpectedAmountText => ExpectedAmount > 0 ? MilitaryFormatting.FormatMoney(ExpectedAmount) : "—";
    public string PaidAmountText => PaidAmount > 0 ? MilitaryFormatting.FormatMoney(PaidAmount) : "—";
    public string DifferenceText => ExpectedAmount > 0 || PaidAmount > 0 ? MilitaryFormatting.FormatMoney(Difference) : "—";
    public string RubricsFound { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string DetailText => string.Join("\n", new[]
    {
        $"{Status} · {Military}",
        $"Aditamento: {Bulletin} · {BulletinDate} · pág. {BulletinPage}",
        $"Tipo: {PaymentType} · {PaymentMode} · Rubrica esperada: {ExpectedRubricPrefix}",
        $"Esperado: {ExpectedAmountText} · Recebido: {PaidAmountText} · Diferença: {DifferenceText}",
        string.IsNullOrWhiteSpace(PaystubPath) ? "Contracheque: não localizado" : $"Contracheque: {PaystubPath}",
        string.IsNullOrWhiteSpace(RubricsFound) ? "Rubricas: —" : $"Rubricas: {RubricsFound}",
        string.Empty,
        Context
    });
}

public sealed class PaymentConferenceRubricHit
{
    public string Military { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string PaystubPath { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Value { get; set; }
    public string ValueText => Value > 0 ? MilitaryFormatting.FormatMoney(Value) : "—";
    public string Prefix => string.IsNullOrWhiteSpace(Code) ? string.Empty : Code[..1].ToUpperInvariant();
    public string Nature => Prefix switch
    {
        "A" => "Atrasado",
        "N" => "Normal",
        "D" => "Desconto",
        _ => "Conferir"
    };
    public string Line { get; set; } = string.Empty;
}

public sealed class PaymentConferenceSummary
{
    public int Expected { get; set; }
    public int Ok { get; set; }
    public int MissingPaystub { get; set; }
    public int MissingRubric { get; set; }
    public int Divergent { get; set; }
    public int Attention { get; set; }
    public int Pending => MissingPaystub + MissingRubric + Divergent + Attention;
    public string ExpectedText => Expected.ToString("N0");
    public string OkText => Ok.ToString("N0");
    public string PendingText => Pending.ToString("N0");
    public string MissingPaystubText => MissingPaystub.ToString("N0");
    public string DivergentText => Divergent.ToString("N0");
}

public sealed class PaymentConferenceResult
{
    public PaymentConferenceSummary Summary { get; set; } = new();
    public List<PaymentConferenceExpectedItem> ExpectedItems { get; set; } = [];
    public List<PaymentConferenceResultRow> Rows { get; set; } = [];
    public List<PaymentConferenceRubricHit> RubricHits { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class PaymentConferenceSettings
{
    [JsonPropertyName("mes")] public int Month { get; set; } = DateTime.Today.Month;
    [JsonPropertyName("ano")] public int Year { get; set; } = DateTime.Today.Year;
    [JsonPropertyName("pasta_contracheques")] public string PaystubFolder { get; set; } = string.Empty;
    [JsonPropertyName("tolerancia_centavos")] public double Tolerance { get; set; } = 0.05;
    [JsonPropertyName("exigir_prefixo")] public bool RequirePrefix { get; set; } = true;
    [JsonPropertyName("conferir_ferias")] public bool IncludeVacation { get; set; } = true;
    [JsonPropertyName("conferir_aux_transporte")] public bool IncludeTransportAid { get; set; } = true;
    [JsonPropertyName("conferir_grat_rep")] public bool IncludeGratification { get; set; } = true;
    [JsonPropertyName("conferir_habilitacao")] public bool IncludeQualification { get; set; } = true;
    [JsonPropertyName("conferir_outros")] public bool IncludeOthers { get; set; } = true;
}
