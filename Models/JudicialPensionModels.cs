using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SIGFUR.Wpf.Models;

public sealed class JudicialPensionExtra : INotifyPropertyChanged
{
    private bool _isIncluded = true;
    private string _type = "R";
    private string _description = string.Empty;
    private decimal _value;
    private bool _isTaxable = true;

    public bool IsIncluded { get => _isIncluded; set => Set(ref _isIncluded, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public decimal Value { get => _value; set => Set(ref _value, value); }
    public bool IsTaxable { get => _isTaxable; set => Set(ref _isTaxable, value); }
    public string TypeLabel => Type == "D" ? "Desconto" : "Receita";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Type)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeLabel)));
    }
}

public sealed class JudicialPensionSettings
{
    public int? MilitaryId { get; set; }
    public string Rank { get; set; } = string.Empty;
    public bool UseManualSalary { get; set; }
    public decimal ManualSalary { get; set; }
    public decimal QualificationPercent { get; set; } = 32m;
    public decimal MilitaryAdditionalPercent { get; set; } = 16m;
    public decimal AvailabilityPercent { get; set; } = 8m;
    public decimal PermanencePercent { get; set; }
    public decimal PreSchoolValue { get; set; }
    public decimal FamilySalaryValue { get; set; }
    public decimal FusexPercent { get; set; } = 3m;
    public decimal MilitaryPension105Percent { get; set; } = 10.5m;
    public decimal MilitaryPension15Percent { get; set; } = 1.5m;
    public decimal FusexDependentDiscount { get; set; }
    public decimal FusexMedicalExpense { get; set; }
    public decimal PnrPercent { get; set; }
    public decimal ExistingAlimony { get; set; }
    public decimal OtherAlimony { get; set; }
    public bool AutomaticIncomeTax { get; set; } = true;
    public decimal ManualIncomeTax { get; set; }
    public int IncomeTaxDependents { get; set; }
    public bool DeductMedicalFromIncomeTax { get; set; }
    public bool DeductFusexDependentFromIncomeTax { get; set; }
    public bool DeductAlimonyFromIncomeTax { get; set; }
    public bool DeductPnrFromIncomeTax { get; set; }
    public bool ApplyIncomeTaxReducer2026 { get; set; }
    public bool FixedPension { get; set; }
    public string PensionBase { get; set; } = "BASE";
    public decimal PensionPercent { get; set; } = 30m;
    public decimal FixedPensionValue { get; set; }
    public List<JudicialPensionExtra> Extras { get; set; } = [];
    public string OutputDirectory { get; set; } = string.Empty;
    public JudicialPensionBulletin Bulletin { get; set; } = new();
}

public sealed class JudicialPensionCalculationInput
{
    public decimal Salary { get; set; }
    public decimal QualificationPercent { get; set; }
    public decimal MilitaryAdditionalPercent { get; set; }
    public decimal AvailabilityPercent { get; set; }
    public decimal PermanencePercent { get; set; }
    public decimal PreSchoolValue { get; set; }
    public decimal FamilySalaryValue { get; set; }
    public decimal FusexPercent { get; set; }
    public decimal MilitaryPension105Percent { get; set; }
    public decimal MilitaryPension15Percent { get; set; }
    public decimal FusexDependentDiscount { get; set; }
    public decimal FusexMedicalExpense { get; set; }
    public decimal PnrPercent { get; set; }
    public decimal ExistingAlimony { get; set; }
    public decimal OtherAlimony { get; set; }
    public bool AutomaticIncomeTax { get; set; }
    public decimal ManualIncomeTax { get; set; }
    public int IncomeTaxDependents { get; set; }
    public bool DeductMedicalFromIncomeTax { get; set; }
    public bool DeductFusexDependentFromIncomeTax { get; set; }
    public bool DeductAlimonyFromIncomeTax { get; set; }
    public bool DeductPnrFromIncomeTax { get; set; }
    public bool ApplyIncomeTaxReducer2026 { get; set; }
    public bool FixedPension { get; set; }
    public string PensionBase { get; set; } = "BASE";
    public decimal PensionPercent { get; set; }
    public decimal FixedPensionValue { get; set; }
    public IReadOnlyList<JudicialPensionExtra> Extras { get; set; } = [];
}

public sealed class JudicialPensionCalculationResult
{
    public decimal Salary { get; set; }
    public decimal QualificationAdditional { get; set; }
    public decimal MilitaryAdditional { get; set; }
    public decimal AvailabilityAdditional { get; set; }
    public decimal PermanenceAdditional { get; set; }
    public decimal GrossEarnings { get; set; }
    public decimal TaxableEarnings { get; set; }
    public decimal Vencimentos { get; set; }
    public decimal Fusex { get; set; }
    public decimal MilitaryPension105 { get; set; }
    public decimal MilitaryPension15 { get; set; }
    public decimal Pnr { get; set; }
    public decimal IncomeTaxBase { get; set; }
    public decimal IncomeTaxBeforeReducer { get; set; }
    public decimal IncomeTaxReducer { get; set; }
    public decimal IncomeTax { get; set; }
    public decimal MandatoryDiscounts { get; set; }
    public decimal PensionCalculationBase { get; set; }
    public decimal JudicialPension { get; set; }
    public decimal NetAfterPension { get; set; }
    public List<(string Description, decimal Value)> EarningsDetail { get; set; } = [];
    public List<(string Description, decimal Value)> DiscountsDetail { get; set; } = [];
    public string Summary { get; set; } = string.Empty;
}

public sealed class JudicialPensionBulletin
{
    public string Subject { get; set; } = "PENSÃO JUDICIAL";
    public string Mode { get; set; } = "IMPLANTAÇÃO";
    public string ProcessNumber { get; set; } = string.Empty;
    public string DecisionType { get; set; } = "Sentença";
    public string DecisionOrigin { get; set; } = string.Empty;
    public string Locality { get; set; } = string.Empty;
    public string Court { get; set; } = string.Empty;
    public string ActionNature { get; set; } = string.Empty;
    public string DecisionDate { get; set; } = string.Empty;
    public string DecisionSummary { get; set; } = string.Empty;
    public string PlaintiffName { get; set; } = string.Empty;
    public string PlaintiffCpf { get; set; } = string.Empty;
    public string ReceivingLetter { get; set; } = string.Empty;
    public string BeneficiaryName { get; set; } = string.Empty;
    public string BeneficiaryCpf { get; set; } = string.Empty;
    public string GuardianName { get; set; } = string.Empty;
    public string GuardianCpf { get; set; } = string.Empty;
    public string Relationship { get; set; } = string.Empty;
    public string PensionType { get; set; } = "Pensão Judicial";
    public string CalculationRule { get; set; } = string.Empty;
    public string VacationIncidence { get; set; } = "Incide";
    public string ChristmasIncidence { get; set; } = "Incide";
    public string CompensationIncidence { get; set; } = "Não incide";
    public string PreviousPensions { get; set; } = "Não há";
    public string BankCode { get; set; } = "001";
    public string BankName { get; set; } = "Banco do Brasil";
    public string Agency { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string BankOperation { get; set; } = string.Empty;
    public string Rubric { get; set; } = "AD0014";
    public string ReferenceMonth { get; set; } = string.Empty;
    public string ReferenceYear { get; set; } = string.Empty;
    public string ArrearsTotalValue { get; set; } = string.Empty;
    public string ExclusionReason { get; set; } = string.Empty;
    public string ExclusionJustification { get; set; } = string.Empty;
    public string PensionEndDate { get; set; } = string.Empty;
    public string LegalBasis { get; set; } = "Art. 529 da Lei nº 13.105/2015 (Código de Processo Civil), Lei nº 5.478/1968 e arts. 14 e 15, inciso VI, da Medida Provisória nº 2.215-10/2001.";
    public string Representation { get; set; } = string.Empty;
    public string Observation { get; set; } = string.Empty;
}

public sealed class JudicialPensionHistoryRecord
{
    public int Id { get; set; }
    public int? MilitaryId { get; set; }
    public string MilitaryName { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public decimal Salary { get; set; }
    public decimal PensionValue { get; set; }
    public decimal PensionPercent { get; set; }
    public string CalculationBase { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Display => $"{CreatedAt:dd/MM/yyyy HH:mm} — {Rank} {MilitaryName} — {MilitaryFormatting.FormatMoney((double)PensionValue)}";
}
