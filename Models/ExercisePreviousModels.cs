using System.Collections.ObjectModel;

namespace SIGFUR.Wpf.Models;

public sealed class ExercisePreviousProcess : INotifyPropertyChanged
{
    private int _id;
    private int? _militaryId;
    private bool _paid;

    public int Id { get => _id; set => Set(ref _id, value); }
    public int? MilitaryId { get => _militaryId; set => Set(ref _militaryId, value); }

    public string Rank { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;

    public string GeneralProtocol { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string SubjectNumber { get; set; } = string.Empty;
    public string SubjectText { get; set; } = string.Empty;
    public string AttachmentSheets { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Object { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string PaymentReason { get; set; } = string.Empty;

    public string EbRequest { get; set; } = string.Empty;
    public string EbInformation { get; set; } = string.Empty;
    public string RefersTo { get; set; } = string.Empty;
    public string RequestedValue { get; set; } = string.Empty;
    public string FormerOdName { get; set; } = string.Empty;
    public string FormerOdIdentity { get; set; } = string.Empty;
    public string FormerOdCpf { get; set; } = string.Empty;
    public string CompanyCommander { get; set; } = string.Empty;
    public string RepresentativeName { get; set; } = string.Empty;
    public string RepresentativeCpf { get; set; } = string.Empty;
    public string RepresentativeIdentity { get; set; } = string.Empty;
    public string BirthDate { get; set; } = string.Empty;

    public string EaIndicative { get; set; } = "Militar de Carreira";
    public string PreviousExerciseType { get; set; } = "AEA - Pagamento de EA tributável";
    public string HasJudicialPension { get; set; } = "Não";
    public string RegistrationFileResearch { get; set; } = "Sim";
    public string FinancialFileResearch { get; set; } = "Sim";
    public string SiafiResearch { get; set; } = "Sim";
    public string RemittanceDocument { get; set; } = string.Empty;

    public string CpexProtocol { get; set; } = string.Empty;
    public string CpexPrintPage { get; set; } = string.Empty;
    public string CpexProtocolledAt { get; set; } = string.Empty;
    public string CpexStatus { get; set; } = string.Empty;
    public string CpexNotes { get; set; } = string.Empty;

    public bool Paid { get => _paid; set => Set(ref _paid, value); }
    public string PaidAt { get; set; } = string.Empty;
    public string PaidNotes { get; set; } = string.Empty;

    public string Situation { get; set; } = "ATIVO";
    public string Bank { get; set; } = string.Empty;
    public string Agency { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;

    public string ProcessNumber { get; set; } = string.Empty;
    public int? ProcessYear { get; set; }
    public string RequestDateInWords { get; set; } = string.Empty;

    public string OrganizationName { get; set; } = string.Empty;
    public string MilitaryRegion { get; set; } = string.Empty;
    public string ManagementUnit { get; set; } = string.Empty;
    public string Codom { get; set; } = string.Empty;
    public string OdNameRank { get; set; } = string.Empty;
    public string OdFunction { get; set; } = string.Empty;
    public string PersonnelChiefNameRank { get; set; } = string.Empty;
    public string PersonnelChiefFunction { get; set; } = string.Empty;
    public string AdministrativeInspectorNameRank { get; set; } = string.Empty;
    public string AdministrativeInspectorFunction { get; set; } = string.Empty;
    public string CityState { get; set; } = string.Empty;

    public string RequestDate { get; set; } = string.Empty;
    public string BulletinNumber { get; set; } = string.Empty;
    public string BulletinDate { get; set; } = string.Empty;
    public string DebtType { get; set; } = string.Empty;
    public string PeriodStart { get; set; } = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string PeriodEnd { get; set; } = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string UpdatedThrough { get; set; } = DateTime.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public string RightMaterializationDocument { get; set; } = ExercisePreviousDefaults.RightMaterializationGuide;
    public string BulletinThatRecorded { get; set; } = ExercisePreviousDefaults.BulletinGuide;
    public string NonPaymentExplanation { get; set; } = ExercisePreviousDefaults.NonPaymentGuide;

    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    public ObservableCollection<ExercisePreviousCode> Codes { get; } = [];
    public ObservableCollection<ExercisePreviousEntry> Entries { get; } = [];

    public string DisplayName => $"{Id:0000} — {Rank} {FullName}".Trim();
    public string ArchiveStatus => Paid ? "Pago / Arquivado" : "Em andamento";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

public sealed class ExercisePreviousCode : INotifyPropertyChanged
{
    private string _description = string.Empty;
    private string _type = "-";
    public int Order { get; set; }
    public string Description { get => _description; set { _description = value ?? string.Empty; PropertyChanged?.Invoke(this, new(nameof(Description))); } }
    public string Type { get => _type; set { _type = string.IsNullOrWhiteSpace(value) ? "-" : value; PropertyChanged?.Invoke(this, new(nameof(Type))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ExercisePreviousEntry : INotifyPropertyChanged
{
    private int _year = DateTime.Today.Year;
    private int _month = DateTime.Today.Month;
    private int _codeOrder = 1;
    private decimal _received;
    private decimal _due;
    public int Id { get; set; }
    public int ProcessId { get; set; }
    public int CodeOrder { get => _codeOrder; set { _codeOrder = value; Changed(nameof(CodeOrder)); Changed(nameof(Competence)); } }
    public int Year { get => _year; set { _year = value; Changed(nameof(Year)); Changed(nameof(Competence)); } }
    public int Month { get => _month; set { _month = value; Changed(nameof(Month)); Changed(nameof(Competence)); } }
    public decimal Received { get => _received; set { _received = value; Changed(nameof(Received)); Changed(nameof(Net)); Changed(nameof(CorrectedReceived)); Changed(nameof(CorrectedNet)); } }
    public decimal Due { get => _due; set { _due = value; Changed(nameof(Due)); Changed(nameof(Net)); Changed(nameof(CorrectedDue)); Changed(nameof(CorrectedNet)); } }
    private decimal _factor = 1m;
    public decimal Factor { get => _factor; set { _factor = value; Changed(nameof(Factor)); Changed(nameof(CorrectedReceived)); Changed(nameof(CorrectedDue)); Changed(nameof(CorrectedNet)); } }
    public decimal CorrectedReceived => Received * Factor;
    public decimal CorrectedDue => Due * Factor;
    public decimal Net => Due - Received;
    public decimal CorrectedNet => CorrectedDue - CorrectedReceived;
    public string Competence => $"{Year:0000}-{Month:00}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}


public sealed class ExercisePreviousIpcaRow
{
    public string Competence { get; set; } = string.Empty;
    public double? Percentage { get; set; }
    public double Factor { get; set; } = 1d;
    public string DisplayCompetence => Competence;
}

public sealed class ExercisePreviousSummary
{
    public decimal Received { get; set; }
    public decimal Due { get; set; }
    public decimal Net { get; set; }
    public decimal CorrectedReceived { get; set; }
    public decimal CorrectedDue { get; set; }
    public decimal CorrectedNet { get; set; }
}

public sealed class ExercisePreviousMilitarySearchResult
{
    public string Token { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int? ActiveMilitaryId { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string BirthDate { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Bank { get; set; } = string.Empty;
    public string Agency { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public int Confidence { get; set; }
    public string MatchKind { get; set; } = string.Empty;
    public bool WeakMatch { get; set; }
    public string DisplayConfidence => Confidence <= 0 ? string.Empty : $"{Confidence}%";
    public string DisplayDocument => string.Join(" | ", new[] { $"CPF {Cpf}", $"Prec-CP {PrecCp}", $"Idt {Identity}" }.Where(x => !x.EndsWith(" ", StringComparison.Ordinal)));
    public string DisplayName => $"{Rank} {FullName} — {WarName} ({Source})".Trim();
}

public sealed class ExercisePreviousImportIssue
{
    public int RowNumber { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RawData { get; set; } = string.Empty;
}

public sealed class ExercisePreviousImportResult
{
    public string FilePath { get; set; } = string.Empty;
    public string SheetName { get; set; } = "Primeira aba";
    public int HeaderRowNumber { get; set; }
    public int TotalRowsRead { get; set; }
    public int Imported { get; set; }
    public int Linked { get; set; }
    public int Pending { get; set; }
    public int Ignored { get; set; }
    public int Errors { get; set; }
    public List<string> MappedHeaders { get; } = [];
    public List<ExercisePreviousImportIssue> Issues { get; } = [];
    public List<ExercisePreviousProcess> Processes { get; } = [];

    public string Summary =>
        $"Arquivo: {Path.GetFileName(FilePath)}{Environment.NewLine}" +
        $"Aba: {SheetName}{Environment.NewLine}" +
        $"Cabecalho: linha {HeaderRowNumber}{Environment.NewLine}" +
        $"Linhas lidas: {TotalRowsRead}{Environment.NewLine}" +
        $"Importados: {Imported}{Environment.NewLine}" +
        $"Vinculados a militares: {Linked}{Environment.NewLine}" +
        $"Pendentes para conferencia: {Pending}{Environment.NewLine}" +
        $"Ignorados: {Ignored}{Environment.NewLine}" +
        $"Erros: {Errors}";

    public string Details
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine(Summary);
            sb.AppendLine();
            sb.AppendLine("Cabecalhos reconhecidos:");
            if (MappedHeaders.Count == 0) sb.AppendLine("- nenhum");
            else foreach (var header in MappedHeaders) sb.AppendLine("- " + header);
            sb.AppendLine();
            sb.AppendLine("Ocorrencias:");
            if (Issues.Count == 0) sb.AppendLine("- nenhuma");
            else foreach (var issue in Issues) sb.AppendLine($"- Linha {issue.RowNumber}: [{issue.Severity}] {issue.Message}");
            return sb.ToString();
        }
    }
}

public sealed class CpexExerciseSettings
{
    public string Browser { get; set; } = "edge";
    public string LoginCpf { get; set; } = string.Empty;
    public string LoginPasswordBase64 { get; set; } = string.Empty;
    public string DriverDirectory { get; set; } = string.Empty;
    public bool KeepBrowserOpen { get; set; } = true;
    public bool Headless { get; set; }
    public int ManualLoginTimeoutSeconds { get; set; } = 300;
    public string OperatorName { get; set; } = string.Empty;
    public string OperatorCpf { get; set; } = string.Empty;
    public string OperatorEmail { get; set; } = string.Empty;
    public string OperatorPhone { get; set; } = string.Empty;

    public string GetPassword()
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(LoginPasswordBase64 ?? string.Empty)); }
        catch { return string.Empty; }
    }
    public void SetPassword(string value) => LoginPasswordBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
}

public sealed class CpexExercisePayload
{
    public string Url { get; set; } = "https://cpex-intranet.eb.mil.br/Exerc_Anterior/sel_opcao.asp";
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CpexExerciseCodeRow> Codes { get; set; } = [];
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CpexExerciseCodeRow
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Original { get; set; } = "0,00";
    public string Corrected { get; set; } = "0,00";
    public string Type { get; set; } = string.Empty;
}

public static class ExercisePreviousDefaults
{
    public static readonly string[] Months = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"];
    public static readonly string[] MilitaryRegions = ["1ª", "2ª", "3ª", "4ª", "5ª", "6ª", "7ª", "8ª", "9ª", "10ª", "11ª", "12ª"];
    public static readonly string[] Situations = ["ATIVO", "AFASTADO", "INATIVO", "RESERVA", "REFORMADO", "DESINCORPORADO", "DESLIGADO"];
    public static readonly string[] Indicatives = ["Militar de Carreira", "Militar Temporário", "Servidor Civil", "Pensionista"];
    public static readonly string[] YesNo = ["Não", "Sim"];
    public static readonly string[] PreviousExerciseTypes =
    [
        "AEA - Pagamento de EA tributável", "A98 - Pagamento de EA não tributável", "AEC - Dev Fusex 3% EA",
        "AED - Dev P MIL EA", "AEE - Dev P MIL 1,5% EA", "AEF - Dev DEP FUSEX",
        "AEG - Pagamento de Auxílio-Fardamento", "AEH - Pagamento de Auxílio-Alimentação",
        "AEI - Pagamento de Auxílio-Natalidade", "AEJ - Pagamento de DEA Auxílio-Transporte - Ativa (DAP)",
        "AEK - Pagamento de DEA Assistência Pré-Escolar - Ativa (DAP)", "AEL - Pagamento de Férias",
        "AEP - Ex Ant RCB Precatório", "AEQ - Adicional Natalino"
    ];

    public const string BulletinGuide = "Boletim Interno da {{OM_NOME}} Nr {{BI_NUMERO}}";
    public const string RightMaterializationGuide = "A solicitação encontra amparo nas Normas para o Pagamento de Despesas de Exercícios Anteriores, no âmbito do Ministério do Exército e nos documentos que materializaram o direito: Parecer da SEF emitido através do DIEx nº 103-ASSE1/SSEF/SEF - CIRCULAR, de 03 MAIO 18, que concluiu: \"(...) c. Em qualquer fase posterior à formação, inclusive durante o EIPOT, os Asp Of R/2 das Armas, do QMB e do Sv Int oriundos dos OFOR farão jus ao adicional de habilitação equivalente a 12% (doze por cento).\", e folhas de alterações autenticadas apresentadas pelo militar referente ao período do Estágio que ora foi realizado no 4º Batalhão de Comunicações.";
    public const string NonPaymentGuide = "A ocorrência da despesa de exercícios anteriores é explicada da seguinte forma: O militar realizou o EIPOT no período de 01 MAR 16 a 15 JUN 16, e não recebeu à época o valor referente a adicional habilitação. No entanto, o Parecer da SEF emitido através do DIEx nº 103-ASSE1/SSEF/SEF - CIRCULAR, de 03 MAIO 18, concluiu que: \"(...) c. Em qualquer fase posterior à formação, inclusive durante o EIPOT, os Asp Of R/2 das Armas, do QMB e do Sv Int oriundos dos OFOR farão jus ao adicional de habilitação equivalente a 12% (doze por cento).\", Desta forma, o militar faz jus ao benefício de 12% relativos à época.";
}
