using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class AdjustmentAccountsSettings : INotifyPropertyChanged
{
    private string _rank = string.Empty;
    private decimal _salary;
    private int _daysInMonth = 30;
    private int _servedDays = 30;
    private int _vacationMonths = 12;
    private int _christmasMonths = 12;
    private bool _vacationAdditionalEntitlement = true;
    private bool _vacationIndemnityEntitlement = true;
    private bool _christmasEntitlement = true;
    private bool _receivedChristmasFirstInstallment;
    private bool _pecuniaryEntitlement;
    private string _selectedBizuTitle = string.Empty;

    public int LastMilitaryId { get; set; }
    public string Rank { get => _rank; set => Set(ref _rank, value ?? string.Empty); }
    public decimal Salary { get => _salary; set => Set(ref _salary, Round(value)); }
    public int DaysInMonth { get => _daysInMonth; set => Set(ref _daysInMonth, Math.Clamp(value, 1, 31)); }
    public int ServedDays { get => _servedDays; set => Set(ref _servedDays, Math.Clamp(value, 0, 31)); }
    public int VacationMonths { get => _vacationMonths; set => Set(ref _vacationMonths, Math.Clamp(value, 0, 12)); }
    public int ChristmasMonths { get => _christmasMonths; set => Set(ref _christmasMonths, Math.Clamp(value, 0, 12)); }

    public bool VacationAdditionalEntitlement { get => _vacationAdditionalEntitlement; set => Set(ref _vacationAdditionalEntitlement, value); }
    public bool VacationIndemnityEntitlement { get => _vacationIndemnityEntitlement; set => Set(ref _vacationIndemnityEntitlement, value); }
    public bool ChristmasEntitlement { get => _christmasEntitlement; set => Set(ref _christmasEntitlement, value); }
    public bool ReceivedChristmasFirstInstallment { get => _receivedChristmasFirstInstallment; set => Set(ref _receivedChristmasFirstInstallment, value); }
    public bool PecuniaryEntitlement { get => _pecuniaryEntitlement; set => Set(ref _pecuniaryEntitlement, value); }
    public string SelectedBizuTitle { get => _selectedBizuTitle; set => Set(ref _selectedBizuTitle, value ?? string.Empty); }

    public decimal QualificationPercent { get; set; } = 12m;
    public decimal MilitaryAdditionalPercent { get; set; } = 13m;
    public decimal MilitaryAvailabilityPercent { get; set; } = 5m;
    public decimal PermanencePercent { get; set; }
    public decimal PreSchoolValue { get; set; }
    public decimal FamilySalaryValue { get; set; }
    public decimal FusexPercent { get; set; } = 3m;
    public decimal MilitaryPensionPercent { get; set; } = 9.5m;
    public decimal PnrPercent { get; set; }
    public decimal FusexDependentDiscount { get; set; }
    public decimal FusexMedicalExpense { get; set; }
    public decimal AlimonyValue { get; set; }

    public int IncomeTaxDependents { get; set; }
    public bool ApplyIncomeTaxReducer2026 { get; set; }
    public bool DeductFusexMedicalExpense { get; set; }
    public bool DeductFusexDependent { get; set; } = true;
    public bool DeductAlimony { get; set; }
    public bool DeductPnr { get; set; }
    public bool IncludeMonthlyIncomeTax { get; set; } = true;
    public bool IncludeVacationIncomeTax { get; set; } = true;
    public bool IncludeChristmasIncomeTax { get; set; } = true;

    public string Organization { get; set; } = "4ª Cia PE";
    public string BulletinNumber { get; set; } = "22";
    public string BulletinDate { get; set; } = DateTime.Today.ToString("dd/MM/yyyy");
    public string CutoffDate { get; set; } = DateTime.Today.ToString("dd/MM/yyyy");
    public string VacationReferenceYear { get; set; } = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
    public string ChristmasReferenceYear { get; set; } = DateTime.Today.Year.ToString(CultureInfo.InvariantCulture);
    public string BulletinReason { get; set; } = "Licenciamento ex officio";
    public string BulletinIntroduction { get; set; } = string.Empty;
    public string BulletinFinalObservation { get; set; } = string.Empty;
    public string SisbolSubject { get; set; } = "Ajuste de Contas";
    public string SisbolSpecificCode { get; set; } = string.Empty;
    public bool BulletinIncludeIntroduction { get; set; } = true;
    public bool BulletinIncludeEarnings { get; set; } = true;
    public bool BulletinIncludeDiscounts { get; set; } = true;
    public bool BulletinIncludeTotals { get; set; }
    public bool BulletinIncludeIdentification { get; set; } = true;
    public bool BulletinIncludeSeparator { get; set; }
    public bool BulletinNumberBatch { get; set; } = true;
    public bool BulletinShowCodes { get; set; }
    public bool BulletinSimplifyDescriptions { get; set; } = true;
    public bool BulletinHideZeroLines { get; set; } = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AdjustmentAccountsSettings Clone()
        => JsonSerializer.Deserialize<AdjustmentAccountsSettings>(JsonSerializer.Serialize(this)) ?? new AdjustmentAccountsSettings();

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

public sealed class AdjustmentRubric : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string _code = string.Empty;
    private string _description = string.Empty;
    private string _reference = "R";
    private string _sign = "+";
    private string _base = "DIA";
    private string _kind = "FIXO";
    private decimal _value;
    private bool _isIncluded = true;
    private decimal _fullValue;
    private decimal _proportionalValue;

    public string Code { get => _code; set => Set(ref _code, (value ?? string.Empty).Trim().ToUpperInvariant()); }
    public string Description { get => _description; set => Set(ref _description, (value ?? string.Empty).Trim()); }
    public string Reference { get => _reference; set => Set(ref _reference, NormalizeReference(value)); }
    public string Sign { get => _sign; set => Set(ref _sign, value == "-" ? "-" : "+"); }
    public string Base { get => _base; set => Set(ref _base, string.Equals(value, "MES", StringComparison.OrdinalIgnoreCase) ? "MES" : "DIA"); }
    public string Kind { get => _kind; set => Set(ref _kind, (value ?? "FIXO").Trim().ToUpperInvariant()); }
    public decimal Value { get => _value; set => Set(ref _value, Round(value)); }
    public bool IsIncluded { get => _isIncluded; set => Set(ref _isIncluded, value); }
    public bool IsCustom { get; set; }
    public bool IsAuto { get; set; }
    public decimal FullValue { get => _fullValue; set { if (Set(ref _fullValue, Round(value))) { OnPropertyChanged(nameof(FullValueText)); } } }
    public decimal ProportionalValue { get => _proportionalValue; set { if (Set(ref _proportionalValue, Round(value))) { OnPropertyChanged(nameof(ProportionalValueText)); } } }

    public string ReferenceText => Reference == "D" ? "Desconto" : "Rendimento";
    public string KindText => Kind switch
    {
        "PERC_SOLDO" or "PERC_VENC" => "%",
        "FORM_FER_PROP" or "FORM_FER_NGOZ" or "FORM_NAT_TOTAL" or "FORM_NAT_1P" or "FORM_NAT_DESC1" => "FÓRMULA",
        "AUTO" => "AUTO",
        _ => "R$"
    };
    public string FullValueText => AdjustmentAccountsService.FormatMoney(FullValue);
    public string ProportionalValueText => AdjustmentAccountsService.FormatMoney(ProportionalValue);

    public event PropertyChangedEventHandler? PropertyChanged;

    public AdjustmentRubric Clone()
        => new()
        {
            Id = Id,
            Code = Code,
            Description = Description,
            Reference = Reference,
            Sign = Sign,
            Base = Base,
            Kind = Kind,
            Value = Value,
            IsIncluded = IsIncluded,
            IsCustom = IsCustom,
            IsAuto = IsAuto,
            FullValue = FullValue,
            ProportionalValue = ProportionalValue
        };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string NormalizeReference(string? value)
        => string.Equals(value?.Trim(), "D", StringComparison.OrdinalIgnoreCase) ? "D" : "R";

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

public sealed class AdjustmentBizuRule
{
    public string Title { get; set; } = string.Empty;
    public bool? VacationAdditional { get; set; }
    public bool? VacationIndemnity { get; set; }
    public bool? ChristmasAdditional { get; set; }
    public bool? Pecuniary { get; set; }
    public string LegalBasis { get; set; } = string.Empty;
    public string Observation { get; set; } = string.Empty;
    public bool IsCustom { get; set; }

    public string VacationAdditionalText => StatusText(VacationAdditional);
    public string VacationIndemnityText => StatusText(VacationIndemnity);
    public string ChristmasAdditionalText => StatusText(ChristmasAdditional);
    public string PecuniaryText => StatusText(Pecuniary);
    public string OriginText => IsCustom ? "Regra editada pelo usuário" : "Padrão do quadro de efeitos pecuniários";

    public AdjustmentBizuRule Clone()
        => new()
        {
            Title = Title,
            VacationAdditional = VacationAdditional,
            VacationIndemnity = VacationIndemnity,
            ChristmasAdditional = ChristmasAdditional,
            Pecuniary = Pecuniary,
            LegalBasis = LegalBasis,
            Observation = Observation,
            IsCustom = IsCustom
        };

    public static string StatusText(bool? value) => value switch { true => "FAZ JUS", false => "NÃO FAZ JUS", _ => "CONFERIR" };
}

public sealed class AdjustmentCalculationResult
{
    public ObservableCollection<AdjustmentRubric> Rows { get; } = [];
    public decimal Earnings { get; set; }
    public decimal Discounts { get; set; }
    public decimal Net => Math.Round(Earnings - Discounts, 2, MidpointRounding.AwayFromZero);
    public decimal AnnualBase { get; set; }
    public decimal FullMonthlyTaxableIncome { get; set; }
    public decimal ProportionalTaxableIncome { get; set; }
    public decimal FullMonthlyIncomeTax { get; set; }
    public decimal ProportionalIncomeTax { get; set; }
}

public sealed class AdjustmentAccountsStore
{
    public AdjustmentAccountsSettings Settings { get; set; } = new();
    public List<AdjustmentRubric> Rubrics { get; set; } = [];
    public List<AdjustmentBizuRule> CustomBizuRules { get; set; } = [];
}

public sealed class SippesRubricRecord
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RubricType { get; set; } = string.Empty;
    public string Nature { get; set; } = string.Empty;
    public string Effect { get; set; } = "R";
    public string EffectText => Effect == "D" ? "Desconto" : "Pagamento/Rendimento";
}

public sealed class BulletinMilitarySelection
{
    public MilitaryRecord Military { get; set; } = new();
    public bool IsSelected { get; set; }
    public string Rank => Military.Rank;
    public string ShortRank => Military.ShortRank;
    public string Name => Military.Name;
    public string WarName => Military.WarName;
    public string PrecCp => Military.PrecCp;
    public string Cpf => Military.FormattedCpf;
}
