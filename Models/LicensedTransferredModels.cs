using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class LicensedTransferredRecord : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public string MilitaryId { get; set; } = string.Empty;
    public string Bank { get; set; } = string.Empty;
    public string Agency { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string PhotoPath { get; set; } = string.Empty;
    public string FormationYear { get; set; } = string.Empty;
    public string BirthDate { get; set; } = string.Empty;
    public string EnlistmentDate { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string ReceivesPreSchool { get; set; } = "Não";
    public string PreSchoolValue { get; set; } = "0.00";
    public string ReceivesTransportAid { get; set; } = "Não";
    public string TransportAidValue { get; set; } = "0.00";
    public string HasPnr { get; set; } = "Não";
    public string Alimony { get; set; } = "Não";
    public string AlimonyValue { get; set; } = string.Empty;
    public double? TransportGrossTotal { get; set; }
    public int? TransportWorkingDays { get; set; }
    public string TransportBaseTimestamp { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Education { get; set; } = string.Empty;

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ShortRank => MilitaryRankService.ShortName(Rank);
    public string FormattedCpf => MilitaryFormatting.FormatCpf(Cpf);
    public string FormattedPrecCp
    {
        get
        {
            var digits = MilitaryFormatting.Digits(PrecCp);
            return digits.Length switch
            {
                9 => $"{digits[..3]}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}",
                10 => $"{digits[..3]}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits[9..]}",
                _ => PrecCp
            };
        }
    }
    public string StatusText => IsVisible ? "Visível" : "Arquivado";
    public string DisplayLabel => $"{ShortRank} {Name}".Trim();
    public string PhotoStatus => !string.IsNullOrWhiteSpace(PhotoPath) && File.Exists(PhotoPath) ? "Foto localizada" : "Sem foto";

    public MilitaryRecord ToMilitaryRecord() => new()
    {
        Id = Id,
        Rank = Rank,
        Name = Name,
        WarName = WarName,
        Cpf = Cpf,
        PrecCp = PrecCp,
        MilitaryId = MilitaryId,
        Bank = Bank,
        Agency = Agency,
        Account = Account,
        PhotoPath = PhotoPath,
        FormationYear = FormationYear,
        BirthDate = BirthDate,
        EnlistmentDate = EnlistmentDate,
        Address = Address,
        ZipCode = ZipCode,
        ReceivesPreSchool = ReceivesPreSchool,
        PreSchoolValue = PreSchoolValue,
        ReceivesTransportAid = ReceivesTransportAid,
        TransportAidValue = TransportAidValue,
        HasPnr = HasPnr,
        Alimony = Alimony,
        AlimonyValue = AlimonyValue,
        TransportGrossTotal = TransportGrossTotal,
        TransportWorkingDays = TransportWorkingDays,
        TransportBaseTimestamp = TransportBaseTimestamp,
        Phone = Phone,
        Email = Email,
        Education = Education
    };

    public LicensedTransferredRecord Clone() => (LicensedTransferredRecord)MemberwiseClone();
}

public sealed class LicensedTransferredPreferences
{
    public bool ShowHidden { get; set; }
    public string CopyTitle { get; set; } = "Copiar formato — Licenciados/Transferidos";
    public string CopyTemplate { get; set; } = "{posto} {nome_upper}\nPrec-CP {prec_cp}  CPF {cpf}";
    public string LastExportDirectory { get; set; } = string.Empty;
}

public sealed class CpexPaystubSettings
{
    public string System { get; set; } = "SIPPES";
    public string Login { get; set; } = string.Empty;
    public bool SavePassword { get; set; } = true;
    public string ProtectedPassword { get; set; } = string.Empty;
    public string Browser { get; set; } = "Edge";
    public bool Headless { get; set; } = true;
    public bool OpenAfterDownload { get; set; } = true;
    public string OutputDirectory { get; set; } = string.Empty;
    public int Year { get; set; } = DateTime.Today.Year;
    public int Month { get; set; } = DateTime.Today.Month;
    public string Processing { get; set; } = "Definitivo";
    public string PayrollType { get; set; } = "Normal";
    public string SheetCode { get; set; } = string.Empty;
}

public sealed record CpexPaystubPerson(
    string Name,
    string Cpf,
    string Rank = "",
    int SourceId = 0,
    string MilitaryId = "",
    string PrecCp = "");

public sealed class CpexPaystubProgress
{
    public int Current { get; init; }
    public int Total { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class CpexPaystubBatchResult
{
    public List<string> DownloadedFiles { get; } = [];
    public List<string> Failures { get; } = [];
    public int Downloaded => DownloadedFiles.Count;
}

public sealed class PaystubDownloadResult
{
    public bool Success { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int Year { get; set; }
    public int Month { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Attempts { get; } = [];
}

public sealed class LicensedTransportFare
{
    public int Index { get; set; }
    public double Fare { get; set; }
    public string Display => Fare.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
}
