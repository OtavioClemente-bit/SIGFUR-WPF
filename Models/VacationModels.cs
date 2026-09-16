using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class VacationPeriod : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private DateTime? _startDate;
    private DateTime? _endDate;
    public int Id { get; set; }
    public int Year { get; set; }
    public int Index { get; set; }
    public bool IsSpecial { get; set; }
    public string Name { get => _name; set => Set(ref _name, value ?? string.Empty); }
    public DateTime? StartDate { get => _startDate; set { if (Set(ref _startDate, value)) OnPropertyChanged(nameof(DateRange)); } }
    public DateTime? EndDate { get => _endDate; set { if (Set(ref _endDate, value)) OnPropertyChanged(nameof(DateRange)); } }
    public string DisplayName
    {
        get
        {
            if (IsSpecial) return string.IsNullOrWhiteSpace(Name) ? "Período Especial" : Name;
            var prefix = $"{Index}º Período";
            return string.IsNullOrWhiteSpace(Name) || Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? prefix : $"{prefix} — {Name}";
        }
    }
    public string DateRange => StartDate is null && EndDate is null ? "Datas não definidas" : $"{StartDate:dd/MM/yyyy} a {EndDate:dd/MM/yyyy}";
    public string FullLabel => $"{DisplayName} · {DateRange}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class VacationMilitaryOption : INotifyPropertyChanged
{
    private int _usedDays;
    public MilitaryRecord Military { get; set; } = new();
    public int UsedDays
    {
        get => _usedDays;
        set
        {
            if (_usedDays == value) return;
            _usedDays = value;
            PropertyChanged?.Invoke(this, new(nameof(UsedDays)));
            PropertyChanged?.Invoke(this, new(nameof(RemainingDays)));
            PropertyChanged?.Invoke(this, new(nameof(DaysText)));
        }
    }
    public int RemainingDays => Math.Max(0, 30 - UsedDays);
    public string DaysText => $"{UsedDays}/30";
    public string Rank => Military.ShortRank;
    public string Name => Military.Name;
    public string WarName => Military.WarName;
    public string SearchText => string.Join(" ", Military.Rank, Military.ShortRank, Military.Name, Military.WarName, Military.Cpf, Military.PrecCp, Military.MilitaryId);
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class VacationAllocation : INotifyPropertyChanged
{
    private int _days;
    private bool _isPaid;
    private DateTime? _paidAt;
    private bool _foodAidPaid;
    private DateTime? _foodAidPaidAt;
    public int Id { get; set; }
    public int Year { get; set; }
    public int PeriodId { get; set; }
    public int MilitaryId { get; set; }
    public MilitaryRecord Military { get; set; } = new();
    public int Days { get => _days; set { if (_days == value) return; _days = value; PropertyChanged?.Invoke(this, new(nameof(Days))); } }
    public bool IsPaid { get => _isPaid; set { if (_isPaid == value) return; _isPaid = value; PropertyChanged?.Invoke(this, new(nameof(IsPaid))); PropertyChanged?.Invoke(this, new(nameof(PaidText))); PropertyChanged?.Invoke(this, new(nameof(PaymentAuditText))); } }
    public DateTime? PaidAt { get => _paidAt; set { if (_paidAt == value) return; _paidAt = value; PropertyChanged?.Invoke(this, new(nameof(PaidAt))); PropertyChanged?.Invoke(this, new(nameof(PaidAtText))); PropertyChanged?.Invoke(this, new(nameof(PaymentAuditText))); } }
    public bool FoodAidPaid { get => _foodAidPaid; set { if (_foodAidPaid == value) return; _foodAidPaid = value; PropertyChanged?.Invoke(this, new(nameof(FoodAidPaid))); PropertyChanged?.Invoke(this, new(nameof(FoodAidText))); PropertyChanged?.Invoke(this, new(nameof(PaymentAuditText))); } }
    public DateTime? FoodAidPaidAt { get => _foodAidPaidAt; set { if (_foodAidPaidAt == value) return; _foodAidPaidAt = value; PropertyChanged?.Invoke(this, new(nameof(FoodAidPaidAt))); PropertyChanged?.Invoke(this, new(nameof(PaymentAuditText))); } }
    public string PaidText => IsPaid ? "Pago" : "Pendente";
    public string PaidAtText => PaidAt is null ? "—" : PaidAt.Value.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
    public bool RequiresVacationFoodAid
    {
        get
        {
            var rank = MilitaryRankService.Normalize(MilitaryRankService.Canonicalize(Military.Rank));
            return rank.Contains("cabo", StringComparison.OrdinalIgnoreCase) || rank.Contains("soldado", StringComparison.OrdinalIgnoreCase);
        }
    }
    public string FoodAidText => RequiresVacationFoodAid ? (FoodAidPaid ? "Aux. alimentação pago" : "Aux. alimentação pendente") : "Não se aplica";
    public string PaymentAuditText => IsPaid
        ? $"Pago em {PaidAtText}" + (RequiresVacationFoodAid ? $" · {FoodAidText}" : string.Empty)
        : "Ainda não pago";
    public string Rank => Military.ShortRank;
    public string Name => Military.Name;
    public string WarName => Military.WarName;
    public string Cpf => Military.FormattedCpf;
    public string PrecCp => Military.PrecCp;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class VacationFinancialProfile
{
    public int MilitaryId { get; set; }
    public int Year { get; set; }
    public string Type { get; set; } = "Temporário";
    public string QualificationCode { get; set; } = QualificationCatalog.NoneCode;
    public decimal? QualificationPercent { get; set; }
    public decimal? MilitaryAdditionalPercent { get; set; }
    public decimal? AvailabilityPercent { get; set; }
    public decimal? PaystubSalary { get; set; }
    public decimal? PaystubQualificationAdditional { get; set; }
    public decimal? PaystubQualificationPercent { get; set; }
    public decimal? PaystubMilitaryAdditional { get; set; }
    public decimal? PaystubAvailabilityAdditional { get; set; }
    public decimal? PaystubMilitaryAdditionalPercent { get; set; }
    public decimal? PaystubAvailabilityPercent { get; set; }
    public decimal? PaystubPnrDiscount { get; set; }
    public bool IncludePnr { get; set; }
    public decimal? PnrPercent { get; set; }
    public decimal? SalaryOverride { get; set; }
    public decimal? QualificationAdditionalOverride { get; set; }
    public decimal? MilitaryAdditionalOverride { get; set; }
    public decimal? AvailabilityAdditionalOverride { get; set; }
    public string PaystubPath { get; set; } = string.Empty;
    public string PaystubReference { get; set; } = string.Empty;
    public DateTime? PaystubModifiedAt { get; set; }
    public string PaystubParserVersion { get; set; } = string.Empty;
    public string PaystubImportError { get; set; } = string.Empty;
    public bool HasPaystubValues => PaystubSalary is > 0m;
    public bool HasAnyAdditionalOverride => SalaryOverride.HasValue
                                            || QualificationAdditionalOverride.HasValue
                                            || MilitaryAdditionalOverride.HasValue
                                            || AvailabilityAdditionalOverride.HasValue;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class PaystubRemunerationSnapshot
{
    public bool Success { get; init; }
    public string Error { get; init; } = string.Empty;
    public decimal Salary { get; init; }
    public decimal QualificationAdditional { get; init; }
    public decimal? QualificationPercent { get; init; }
    public decimal MilitaryAdditional { get; init; }
    public decimal AvailabilityAdditional { get; init; }
    public decimal PermanenceAdditional { get; init; }
    public decimal? MilitaryAdditionalPercent { get; init; }
    public decimal? AvailabilityPercent { get; init; }
    public decimal? PermanencePercent { get; init; }
    public decimal PreSchool { get; init; }
    public decimal FamilySalary { get; init; }
    public decimal Fusex { get; init; }
    public decimal? FusexPercent { get; init; }
    public decimal MilitaryPension { get; init; }
    public decimal? MilitaryPension105Percent { get; init; }
    public decimal? MilitaryPension15Percent { get; init; }
    public decimal FusexDependent { get; init; }
    public decimal FusexMedical { get; init; }
    public decimal PnrDiscount { get; init; }
    public decimal? PnrPercent { get; init; }
    public decimal ExistingAlimony { get; init; }
    public decimal IncomeTax { get; init; }
}

public sealed class VacationFinancialResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public decimal Salary { get; set; }
    public decimal QualificationAdditional { get; set; }
    public decimal MilitaryAdditional { get; set; }
    public decimal AvailabilityAdditional { get; set; }
    public decimal BaseTotal { get; set; }
    public decimal VacationAdditional { get; set; }
    public decimal PnrDiscount { get; set; }
    public string VacationAdditionalText => VacationAdditional.ToString("C2", CultureInfo.GetCultureInfo("pt-BR"));
    public string VacationAdditionalWords => Success ? NumberToWordsService.Convert(VacationAdditional, true) : string.Empty;
}

public sealed class VacationPreferences
{
    public int LastYear { get; set; } = DateTime.Today.Year;
    public int LastPeriodId { get; set; }
    public int LastTab { get; set; }
    public string Search { get; set; } = string.Empty;
    public string AllocationSearch { get; set; } = string.Empty;
    public string AllocationStatus { get; set; } = "Todos";
    public string Rank { get; set; } = "Todos";
    public string SortMode { get; set; } = "Posto/Graduação";
    public bool AvailableOnly { get; set; } = true;
    public int DefaultDays { get; set; } = 30;
    public string LastModel { get; set; } = string.Empty;
    public string SpecificSubjectCode { get; set; } = string.Empty;
    public string SpecificSubjectName { get; set; } = "ADICIONAL FÉRIAS - Ordem de Saque";
    public Dictionary<string, string> FormFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class VacationSavedBulletin
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
    public string Title { get; set; } = "Boletim de férias";
    public string Text { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public int Year { get; set; }
    public int PeriodId { get; set; }
    public List<int> MilitaryIds { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string DisplayLabel => $"{Title} · {UpdatedAt:dd/MM/yyyy HH:mm}";
}

public sealed class VacationBulletinStore
{
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VacationSavedBulletin> Saved { get; set; } = [];
}

public sealed class VacationOverview
{
    public ObservableCollection<VacationPeriod> Periods { get; } = [];
    public ObservableCollection<MilitaryRecord> AvailableMilitary { get; } = [];
    public ObservableCollection<VacationAllocation> Allocations { get; } = [];
}
