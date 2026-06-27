using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SIGFUR.Wpf.Models;

public sealed class PlanCallRecord : INotifyPropertyChanged
{
    private string _rank = string.Empty;
    private string _name = string.Empty;
    private string _warName = string.Empty;
    private string _cpf = string.Empty;
    private string _precCp = string.Empty;
    private string _phone = string.Empty;
    private string _alternatePhone = string.Empty;
    private string _street = string.Empty;
    private string _number = string.Empty;
    private string _complement = string.Empty;
    private string _district = string.Empty;
    private string _cityState = string.Empty;
    private string _zipCode = string.Empty;
    private string _importSource = string.Empty;

    // Chave do banco PRÓPRIO do Plano de Chamada.
    public int Id { get; set; }

    // Vínculo transitório encontrado no Listar Militares. Nunca é usado como chave
    // do Plano; serve apenas para comparar endereço e completar telefone ausente.
    public int MilitaryId { get; set; }
    public bool HasMilitaryMatch => MilitaryId > 0;

    public string Rank { get => _rank; set => Set(ref _rank, value ?? string.Empty); }
    public string ShortRank => Services.MilitaryRankService.ShortName(Rank);
    public string Name { get => _name; set => Set(ref _name, value ?? string.Empty); }
    public string WarName { get => _warName; set => Set(ref _warName, value ?? string.Empty); }
    public string Cpf { get => _cpf; set => Set(ref _cpf, value ?? string.Empty); }
    public string FormattedCpf => MilitaryFormatting.FormatCpf(Cpf);
    public string PrecCp { get => _precCp; set => Set(ref _precCp, value ?? string.Empty); }

    public string BasePhone { get; set; } = string.Empty;
    public string BaseEmail { get; set; } = string.Empty;
    public string BaseAddress { get; set; } = string.Empty;
    public string BaseZipCode { get; set; } = string.Empty;
    public bool HasOverride { get; set; } = true;

    public string Phone { get => _phone; set => Set(ref _phone, value ?? string.Empty); }
    public string AlternatePhone { get => _alternatePhone; set => Set(ref _alternatePhone, value ?? string.Empty); }
    public string Street { get => _street; set { if (Set(ref _street, value ?? string.Empty)) NotifyAddress(); } }
    public string Number { get => _number; set { if (Set(ref _number, value ?? string.Empty)) NotifyAddress(); } }
    public string Complement { get => _complement; set { if (Set(ref _complement, value ?? string.Empty)) NotifyAddress(); } }
    public string District { get => _district; set { if (Set(ref _district, value ?? string.Empty)) NotifyAddress(); } }
    public string CityState { get => _cityState; set { if (Set(ref _cityState, value ?? string.Empty)) NotifyAddress(); } }
    public string ZipCode { get => _zipCode; set { if (Set(ref _zipCode, value ?? string.Empty)) NotifyAddress(); } }
    public string ImportSource { get => _importSource; set => Set(ref _importSource, value ?? string.Empty); }

    public string PlanAddress => Services.PlanCallService.FormatAddress(Street, Number, Complement, District, CityState, string.Empty);
    public bool HasPlanAddress => !string.IsNullOrWhiteSpace(PlanAddress);
    public string EffectivePhone => string.IsNullOrWhiteSpace(Phone) ? BasePhone : Phone;
    public string EffectiveZipCode => string.IsNullOrWhiteSpace(ZipCode) ? BaseZipCode : ZipCode;
    public string EffectiveAddress => HasPlanAddress ? PlanAddress : BaseAddress;
    public string Region => Services.PlanCallService.RegionFor(EffectiveAddress, CityState);
    public string RecordSource => Id > 0 ? "Plano importado" : "Cadastro do Listar";
    public string MatchStatus => HasMilitaryMatch ? "Localizado no Listar" : "Não localizado";
    public string DifferenceStatus => !HasMilitaryMatch ? "SEM COMPARAÇÃO" :
        !HasPlanAddress ? "SEM ENDEREÇO NO PLANO" :
        string.IsNullOrWhiteSpace(BaseAddress) ? "SEM ENDEREÇO NO LISTAR" :
        Services.PlanCallService.AddressEquivalent(BaseAddress, PlanAddress) ? "CONFERE" : "DIVERGENTE";
    public string PhoneStatus => !HasMilitaryMatch ? "Sem comparação" :
        string.IsNullOrWhiteSpace(Phone) ? (string.IsNullOrWhiteSpace(BasePhone) ? "SEM TELEFONE" : "Somente no Listar") :
        string.IsNullOrWhiteSpace(BasePhone) ? "Pode completar o Listar" :
        Services.PlanCallService.PhoneEquivalent(BasePhone, Phone) ? "Confere" : "Diferente";
    public bool CanCopyPhoneToMilitary => HasMilitaryMatch && string.IsNullOrWhiteSpace(BasePhone) && !string.IsNullOrWhiteSpace(Phone);
    public string SearchText => $"{Rank} {ShortRank} {Name} {WarName} {Cpf} {PrecCp} {Phone} {AlternatePhone} {BasePhone} {PlanAddress} {BaseAddress} {ZipCode} {Region} {RecordSource} {DifferenceStatus} {PhoneStatus}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(Rank)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortRank)));
        if (name is nameof(Cpf)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormattedCpf)));
        if (name is nameof(Phone))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectivePhone)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PhoneStatus)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCopyPhoneToMilitary)));
        }
        return true;
    }

    public void RefreshComparison()
    {
        foreach (var name in new[] { nameof(MatchStatus), nameof(DifferenceStatus), nameof(PhoneStatus), nameof(CanCopyPhoneToMilitary), nameof(SearchText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void NotifyAddress()
    {
        foreach (var name in new[] { nameof(PlanAddress), nameof(HasPlanAddress), nameof(EffectiveAddress), nameof(EffectiveZipCode), nameof(Region), nameof(RecordSource), nameof(DifferenceStatus), nameof(SearchText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class PlanCallImportRow
{
    public int SourceRow { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;
    public string Cpf { get; set; } = string.Empty;
    public string PrecCp { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string AlternatePhone { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public string Complement { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string CityState { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string RawAddress { get; set; } = string.Empty;
}

public sealed class PlanCallImportMatch
{
    public PlanCallImportRow Imported { get; set; } = new();
    public PlanCallRecord? Current { get; set; }
    public string MatchKind { get; set; } = "Novo no Plano";
    public string ChangeKind { get; set; } = "Novo";
    public double Confidence { get; set; }
    public bool Apply { get; set; } = true;
    public string MilitaryText => Current is null ? Imported.Name : $"{Current.ShortRank} {Current.Name}";
    public string ImportedAddress => Services.PlanCallService.FormatAddress(Imported.Street, Imported.Number, Imported.Complement, Imported.District, Imported.CityState, Imported.RawAddress);
    public string CurrentAddress => Current?.PlanAddress ?? string.Empty;
}

public sealed class PlanCallRestorePoint
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Display => $"#{Id} — {CreatedAt:dd/MM/yyyy HH:mm} — {Description}";
}

public sealed class ViaCepAddress
{
    public string ZipCode { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string CityState => string.IsNullOrWhiteSpace(City) ? string.Empty : $"{City}/{State}";
    public string Display => $"{Services.PlanCallService.FormatZipCode(ZipCode)} — {Street} — {District} — {CityState}".Replace(" —  —", " —").Trim(' ', '—');
}

public sealed class PlanCallSettings
{
    public string Search { get; set; } = string.Empty;
    public string SortMode { get; set; } = "Hierarquia";
    public string OutputDirectory { get; set; } = string.Empty;
    public bool GroupByRegion { get; set; }
    public bool ShowOnlyDifferences { get; set; }
}

public sealed class AddressParts
{
    public string Street { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public string Complement { get; set; } = string.Empty;
    public string District { get; set; } = string.Empty;
    public string CityState { get; set; } = string.Empty;
}
