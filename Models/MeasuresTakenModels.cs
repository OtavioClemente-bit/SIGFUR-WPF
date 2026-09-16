using System.ComponentModel;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class MeasuresMilitaryItem
{
    public required MilitaryRecord Military { get; init; }
    public string Source { get; init; } = MeasuresTakenService.SourceActive;
    public bool IsTransferred { get; init; }
    public string Rank => Military.ShortRank;
    public string Name => Military.Name;
    public string WarName => Military.WarName;
    public string PrecCp => Military.PrecCp;
    public string Cpf => Military.FormattedCpf;
    public string SearchText => $"{Source} {Military.Rank} {Military.ShortRank} {Military.Name} {Military.WarName} {Military.PrecCp} {Military.Cpf} {Military.MilitaryId}";
}

public sealed class MeasuresSelectedItem : INotifyPropertyChanged
{
    private string _section = MeasuresSections.PaymentExam;
    private string _individualMeasure = string.Empty;
    private int _order;
    private bool _isMeasureMarked;

    public required MeasuresMilitaryItem Person { get; init; }
    public string Section
    {
        get => _section;
        set { if (_section == value) return; _section = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Section))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SectionLabel))); }
    }
    public string IndividualMeasure
    {
        get => _individualMeasure;
        set { if (_individualMeasure == value) return; _individualMeasure = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndividualMeasure))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMeasure))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MeasureStatus))); }
    }
    public int Order
    {
        get => _order;
        set { if (_order == value) return; _order = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Order))); }
    }
    public bool IsMeasureMarked
    {
        get => _isMeasureMarked;
        set { if (_isMeasureMarked == value) return; _isMeasureMarked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMeasureMarked))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MarkStatus))); }
    }
    public string Rank => Person.Rank;
    public string Name => Person.Name;
    public string WarName => Person.WarName;
    public string PrecCp => Person.PrecCp;
    public string Source => Person.Source;
    public string SectionLabel => Section == MeasuresSections.PaystubExam ? "Exame de Contracheque" : "Exame de Pagamento";
    public bool HasMeasure => !string.IsNullOrWhiteSpace(IndividualMeasure);
    public string MeasureStatus => HasMeasure ? "PREENCHIDA" : "PENDENTE";
    public string MarkStatus => IsMeasureMarked ? "MARCADO" : string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class MeasuresSections
{
    public const string PaymentExam = "relatorio_nominal";
    public const string PaystubExam = "exame_contracheque";
}

public sealed class MeasuresTakenSettings
{
    public string Source { get; set; } = MeasuresTakenService.SourceAll;
    public string Search { get; set; } = string.Empty;
    public string OriginText { get; set; } = "Relatório do Exame de Pagamento de Pessoal";
    public string Organization { get; set; } = string.Empty;
    public string DefaultMeasure { get; set; } = string.Empty;
    public string PaymentDefaultMeasure { get; set; } = string.Empty;
    public string PaystubDefaultMeasure { get; set; } = string.Empty;
    public int LastActiveTab { get; set; }
    public string CommanderName { get; set; } = string.Empty;
    public string CommanderRank { get; set; } = string.Empty;
    public string SignatureRole { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public int? LastWorkId { get; set; }
    public string SpedSender { get; set; } = "PHABLLO";
    public string SpedRecipient { get; set; } = "Aj G";
    public string SpedClassification { get; set; } = string.Empty;
    public bool SpedFillPurpose { get; set; }
    public string SpedPurpose { get; set; } = "Geral";
    public string SpedSubject { get; set; } = "resposta ao exame de pagamento";
    public string SpedContact { get; set; } = string.Empty;
    public string SpedRitex { get; set; } = string.Empty;
    public string SpedEmail { get; set; } = string.Empty;
}

public sealed class MeasuresSavedWorkSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string UpdatedText => UpdatedAt == default ? "—" : UpdatedAt.ToString("dd/MM/yyyy HH:mm");
}

public sealed class MeasuresSavedWorkPayload
{
    public string OriginText { get; set; } = string.Empty;
    public string Organization { get; set; } = string.Empty;
    public string DefaultMeasure { get; set; } = string.Empty;
    public string PaymentDefaultMeasure { get; set; } = string.Empty;
    public string PaystubDefaultMeasure { get; set; } = string.Empty;
    public string CommanderName { get; set; } = string.Empty;
    public string CommanderRank { get; set; } = string.Empty;
    public string SignatureRole { get; set; } = string.Empty;
    public List<MeasuresSavedPerson> People { get; set; } = [];
    public string SpedSender { get; set; } = "PHABLLO";
    public string SpedRecipient { get; set; } = "Aj G";
    public string SpedClassification { get; set; } = string.Empty;
    public bool SpedFillPurpose { get; set; }
    public string SpedPurpose { get; set; } = "Geral";
    public string SpedSubject { get; set; } = "resposta ao exame de pagamento";
    public string SpedContact { get; set; } = string.Empty;
    public string SpedRitex { get; set; } = string.Empty;
    public string SpedEmail { get; set; } = string.Empty;
    public List<string> SpedAttachmentFiles { get; set; } = [];
}

public sealed class MeasuresSavedPerson
{
    public int MilitaryId { get; set; }
    public bool IsTransferred { get; set; }
    public string Section { get; set; } = MeasuresSections.PaymentExam;
    public string IndividualMeasure { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
}

public sealed class MeasuresPdfEntry
{
    public string Section { get; set; } = MeasuresSections.PaymentExam;
    public int Order { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Changes { get; set; } = string.Empty;
}

public sealed class MeasuresPdfImportResult
{
    public List<MeasuresPdfEntry> Entries { get; set; } = [];
    public string SuggestedOrigin { get; set; } = string.Empty;
    public string ExtractedText { get; set; } = string.Empty;
}

public sealed class MeasuresDocumentData
{
    public string OriginText { get; set; } = string.Empty;
    public string Organization { get; set; } = string.Empty;
    public string DefaultMeasure { get; set; } = string.Empty;
    public string PaymentDefaultMeasure { get; set; } = string.Empty;
    public string PaystubDefaultMeasure { get; set; } = string.Empty;
    public string CommanderName { get; set; } = string.Empty;
    public string CommanderRank { get; set; } = string.Empty;
    public string SignatureRole { get; set; } = string.Empty;
    public IReadOnlyList<MeasuresSelectedItem> Items { get; set; } = [];
}

public sealed class BankInconsistencySettings
{
    public string SystemName { get; set; } = "SIPPES";
    public int Year { get; set; } = DateTime.Today.Year;
    public string Month { get; set; } = "TODOS";
    public string MilitaryRegion { get; set; } = "TODAS";
    public string Codom { get; set; } = string.Empty;
    public List<string> CodomHistory { get; set; } = [];
    public string ReportType { get; set; } = "Erro Ag/Conta";
    public string OutputDirectory { get; set; } = string.Empty;
    public bool DownloadSpreadsheet { get; set; } = true;
    public bool OpenPdf { get; set; } = true;
    public bool Headless { get; set; } = true;
    public bool KeepBrowserOpen { get; set; }
    public string LastPdf { get; set; } = string.Empty;
    public string LastSpreadsheet { get; set; } = string.Empty;
}

public sealed class SinfoppesCriticizedSettings
{
    public string Cpf { get; set; } = string.Empty;
    public string ProtectedPassword { get; set; } = string.Empty;
    public bool SaveCpf { get; set; } = true;
    public bool SavePassword { get; set; }
    public int Year { get; set; } = DateTime.Today.Year;
    public string Month { get; set; } = DateTime.Today.ToString("MMMM", CultureInfo.GetCultureInfo("pt-BR")).ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
    public string Run { get; set; } = "3ª CORRIDA";
    public string OutputDirectory { get; set; } = string.Empty;
    public bool OpenPdf { get; set; } = true;
    public bool Headless { get; set; } = true;
    public bool KeepBrowserOpen { get; set; }
    public string LastPdf { get; set; } = string.Empty;

    public string GetPassword() => WindowsSecretProtector.Unprotect(ProtectedPassword);
    public void SetPassword(string value) => ProtectedPassword = SavePassword ? WindowsSecretProtector.Protect(value) : string.Empty;
}

public sealed class AutomationProgress
{
    public int Percent { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class BankAutomationResult
{
    public bool Success { get; set; }
    public bool NoRecords { get; set; }
    public string Message { get; set; } = string.Empty;
    public string PdfPath { get; set; } = string.Empty;
    public string SpreadsheetPath { get; set; } = string.Empty;
    public int RecordCount { get; set; }
}
