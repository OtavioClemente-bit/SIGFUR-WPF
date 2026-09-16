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
    public string Source { get; set; } = "Aditamento Furriel";
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
    public List<string> ExpectedCodes { get; set; } = [];
    public string ExpectedCodesText => string.Join(" / ", ExpectedCodes);
    public string ReviewReason { get; set; } = string.Empty;
    public bool AdministrativeOnly { get; set; }
    public int? PaymentMonth { get; set; }
    public int? PaymentYear { get; set; }
    public string ReferencePeriod { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public double ExpectedAmount { get; set; }
    public string Context { get; set; } = string.Empty;
    public string MatchedMilitaryName { get; set; } = string.Empty;
    public int? MatchedMilitaryId { get; set; }
    public string MatchStatus { get; set; } = "Não conferido";
    public string Origin { get; set; } = "PARSER_SIGFUR";
    public double ParserConfidence { get; set; } = 1;
    public string ExpectedAmountText => ExpectedAmount > 0 ? MilitaryFormatting.FormatMoney(ExpectedAmount) : "—";
    public string PageText => $"Pág. {Page}";
    public string IdentityText => string.Join(" · ", new[] { Rank, Name }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed class PaymentConferenceResultRow : System.ComponentModel.INotifyPropertyChanged
{
    public string ItemId { get; set; } = Guid.NewGuid().ToString("N");
    public string Origin { get; set; } = "PARSER_SIGFUR";
    public string PublishedName { get; set; } = "";
    public string HighlightName => string.IsNullOrWhiteSpace(PublishedName) ? Military.Split('·').Last().Trim() : PublishedName;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private bool _isVerified;
    public bool IsVerified
    {
        get => _isVerified;
        set { if (_isVerified == value) return; _isVerified = value;
            foreach (var name in new[] { nameof(IsVerified), nameof(VerificationText), nameof(VerificationActionText), nameof(ReviewLabel), nameof(DetailText) })
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name)); }
    }
    public string VerificationText => IsVerified ? "✓ Verificado" : "Não verificado";
    public string VerificationActionText => IsVerified ? "✓ Verificado — desmarcar" : "Marcar como verificado";
    public bool PresenceOnly { get; set; }
    public string ReviewLabel => $"Adt {Bulletin} · pág. {BulletinPage}\n{Military}\n{PaymentType} · {Status}\n{VerificationText}";
    public string ConferencePeriod { get; set; } = string.Empty;
    public string ExpectedCodesText { get; set; } = string.Empty;
    public string RuleSource { get; set; } = string.Empty;
    public string IdentityEvidence { get; set; } = string.Empty;
    public string ReferencePeriod { get; set; } = string.Empty;
    public string OtherRubrics { get; set; } = string.Empty;
    public bool HasExpectedAmount { get; set; }
    public bool HasPaidAmount { get; set; }
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
    public string ExpectedAmountText => HasExpectedAmount ? MilitaryFormatting.FormatMoney(ExpectedAmount) : "—";
    public string PaidAmountText => HasPaidAmount ? MilitaryFormatting.FormatMoney(PaidAmount) : "—";
    public string DifferenceText => HasExpectedAmount && HasPaidAmount ? MilitaryFormatting.FormatMoney(Difference) : "—";
    public string RubricsFound { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string DetailText => string.Join("\n", new[]
    {
        $"{Status} · {Military}",
        VerificationText,
        $"Competência da folha: {ConferencePeriod}",
        Notes,
        $"Aditamento: {Bulletin} · {BulletinDate} · pág. {BulletinPage}",
        $"Tipo: {PaymentType} · {PaymentMode} · Esperado: {ExpectedCodesText}",
        $"Publicado: {ExpectedAmountText} · No contracheque: {PaidAmountText} · Diferença: {DifferenceText}",
        $"Referência do direito: {ReferencePeriod}",
        IdentityEvidence,
        RuleSource,
        string.IsNullOrWhiteSpace(PaystubPath) ? "Contracheque: não localizado" : $"Contracheque: {PaystubPath}",
        string.IsNullOrWhiteSpace(RubricsFound) ? "Rubricas: —" : $"Rubricas: {RubricsFound}",
        string.IsNullOrWhiteSpace(OtherRubrics) ? string.Empty : $"Outras rubricas no PDF: {OtherRubrics}",
        string.Empty,
        Context
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
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
    public string Nature => (Code.Length >= 2 ? Code[..2].ToUpperInvariant() : Prefix) switch
    {
        "AR" => "Receita atrasada",
        "NR" => "Receita normal",
        "ND" => "Desconto normal",
        "DR" => "Devolução de receita",
        "ER" => "Receita de exercício anterior",
        "ED" => "Desconto de exercício anterior",
        "FR" => "Diferença de receita",
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
    public int NotApplicable { get; set; }
    public int Pending => MissingPaystub + MissingRubric + Divergent + Attention;
    public string ExpectedText => Expected.ToString("N0");
    public string OkText => Ok.ToString("N0");
    public string PendingText => Pending.ToString("N0");
    public string MissingPaystubText => MissingPaystub.ToString("N0");
    public string MissingRubricText => MissingRubric.ToString("N0");
    public string DivergentText => Divergent.ToString("N0");
}

public sealed class PaymentConferenceResult
{
    public bool AcceptRubricPresence { get; set; }
    public int Month { get; set; }
    public int Year { get; set; }
    public int BulletinCount { get; set; }
    public int OtherPeriodFiles { get; set; }
    public int UnclassifiedFiles { get; set; }
    public List<PaymentConferencePaystubFile> PaystubFiles { get; set; } = [];
    public string ScopeText => Year == 0 ? "Escolha mês/ano da folha e marque os boletins para conferir."
        : $"Folha {Month:00}/{Year}: {PaystubFiles.Count} contracheque(s) · {BulletinCount} boletim(ns) marcado(s) · {OtherPeriodFiles} PDF(s) de outras competências excluído(s)"
          + (UnclassifiedFiles > 0 ? $" · {UnclassifiedFiles} PDF(s) sem competência confirmada, fora da conferência" : "")
          + (AcceptRubricPresence ? " · Critério: presença por código ou nome" : " · Critério: código, natureza e valor");
    public string InputSignature { get; set; } = string.Empty;
    public PaymentConferenceSummary Summary { get; set; } = new();
    public List<PaymentConferenceExpectedItem> ExpectedItems { get; set; } = [];
    public List<PaymentConferenceResultRow> Rows { get; set; } = [];
    public List<PaymentConferenceRubricHit> RubricHits { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public PaymentMonthlyInventory Inventory { get; set; } = new();
    public List<PaymentAmbiguousBlock> AmbiguousBlocks { get; set; } = [];
    public List<PaymentRubricChange> RubricChanges { get; set; } = [];
}

public sealed class PaymentMonthlyInventory
{
    public int ExpectedBulletins { get; set; }
    public int ProcessedBulletins { get; set; }
    public int ExpectedPages { get; set; }
    public int ProcessedPages { get; set; }
    public int ExpectedPaystubs { get; set; }
    public int FoundPaystubs { get; set; }
    public int ReadPaystubs { get; set; }
    public int ExpectedPreviousPaystubs { get; set; }
    public int FoundPreviousPaystubs { get; set; }
    public List<string> MissingDocuments { get; set; } = [];
    public List<string> UnreadableDocuments { get; set; } = [];
    public bool Complete => ExpectedBulletins > 0
                            && ProcessedBulletins == ExpectedBulletins
                            && ProcessedPages == ExpectedPages
                            && FoundPaystubs >= ExpectedPaystubs
                            && ReadPaystubs == FoundPaystubs
                            && (ExpectedPreviousPaystubs == 0 || FoundPreviousPaystubs >= ExpectedPreviousPaystubs)
                            && MissingDocuments.Count == 0 && UnreadableDocuments.Count == 0;
    public double CoveragePercent
    {
        get
        {
            var expected = ExpectedBulletins + ExpectedPages + ExpectedPaystubs + ExpectedPreviousPaystubs;
            var actual = Math.Min(ExpectedBulletins, ProcessedBulletins)
                         + Math.Min(ExpectedPages, ProcessedPages)
                         + Math.Min(ExpectedPaystubs, ReadPaystubs)
                         + Math.Min(ExpectedPreviousPaystubs, FoundPreviousPaystubs);
            return expected <= 0 ? 0 : Math.Round(actual * 100d / expected, 1);
        }
    }
}

public sealed class PaymentAmbiguousBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceFile { get; set; } = string.Empty;
    public string BulletinNumber { get; set; } = string.Empty;
    public string BulletinDate { get; set; } = string.Empty;
    public int Page { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class PaymentRubricChange
{
    public string ItemId { get; set; } = Guid.NewGuid().ToString("N");
    public string Cpf { get; set; } = string.Empty;
    public string Military { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public double? PreviousValue { get; set; }
    public double? CurrentValue { get; set; }
    public string CurrentPaystubPath { get; set; } = string.Empty;
    public bool HasBulletinEvidence { get; set; }
}

public sealed class PaymentMonthlyAuditEstimate
{
    public int Bulletins { get; set; }
    public int AmbiguousBlocks { get; set; }
    public int SemanticDivergences { get; set; }
    public int CachedCalls { get; set; }
    public int EstimatedCalls { get; set; }
    public int EstimatedInputTokens { get; set; }
    public int EstimatedOutputTokens { get; set; }
    public decimal EstimatedCostBrl { get; set; }
}

public sealed class PaymentMonthlyAuditPreparation
{
    public PaymentConferenceResult Conference { get; set; } = new();
    public PaymentMonthlyAuditEstimate Estimate { get; set; } = new();
    public List<string> BulletinPaths { get; set; } = [];
    public PaymentConferenceSettings Settings { get; set; } = new();
}

public sealed class PaymentMonthlyAuditResult
{
    public int Month { get; set; }
    public int Year { get; set; }
    public PaymentMonthlyInventory Inventory { get; set; } = new();
    public int DeterministicPublications { get; set; }
    public int AiRescuedPublications { get; set; }
    public int ItemsExpected { get; set; }
    public int ItemsReturned { get; set; }
    public bool StructuredCoverageComplete { get; set; }
    public List<PaymentAiFinding> Findings { get; set; } = [];
    public List<PaymentMonthlyMilitaryLedger> Ledgers { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public decimal CostBrl { get; set; }
    public bool Complete => Inventory.Complete && StructuredCoverageComplete && ItemsExpected == ItemsReturned;
    public string Status => Complete ? "AUDITORIA COMPLETA" : $"AUDITORIA PARCIAL — COBERTURA {Inventory.CoveragePercent:0.#}%";
}

public sealed class PaymentMonthlyMilitaryLedger
{
    public string Military { get; set; } = string.Empty;
    public int BulletinItems { get; set; }
    public int PaystubItems { get; set; }
    public decimal NetDifference { get; set; }
    public List<PaymentAiFinding> Findings { get; set; } = [];
}

public sealed class PaymentConferencePaystubFile
{
    public string Path { get; set; } = string.Empty;
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Cpf { get; set; } = string.Empty;
    public int Month { get; set; }
    public int Year { get; set; }
    public string Period => $"{Month:00}/{Year}";
}

public sealed class PaymentConferenceSettings
{
    [JsonPropertyName("aceitar_presenca_por_nome")] public bool AcceptRubricPresence { get; set; } = true;
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
