using System.Globalization;

namespace SIGFUR.Wpf.Models;

public sealed class SalaryRecord
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public string Rank { get; set; } = string.Empty;
    public decimal Salary { get; set; }
    public decimal Official2026 { get; set; }
    public bool IsHidden { get; set; }
    public bool HasOfficialReference => Official2026 > 0;
    public decimal Difference => Salary - Official2026;
    public string ShortRank => Services.MilitaryRankService.ShortName(Rank);
    public string SalaryText => Salary.ToString("C2", PtBr);
    public string Official2026Text => Official2026 > 0 ? Official2026.ToString("C2", PtBr) : "—";
    public string DifferenceText => Official2026 > 0 ? Difference.ToString("C2", PtBr) : "—";
    public string StatusText => Salary <= 0 ? "Sem valor" : Official2026 <= 0 ? "Personalizado" : Math.Abs(Difference) < 0.005m ? "Conferido" : "Diferente da referência";
}

public sealed class SalaryHiddenStore
{
    public List<string> Hidden { get; set; } = [];
}

public sealed class GratificationSettings
{
    public string Destination { get; set; } = "Guarnição do Rio de Janeiro";
    public string Purpose { get; set; } = "competir nas Olimpíadas Militares do CML";
    public DateTime DepartureDate { get; set; } = DateTime.Today;
    public string DepartureTime { get; set; } = "06:00";
    public DateTime ReturnDate { get; set; } = DateTime.Today;
    public string ReturnTime { get; set; } = "21:00";
    public string BulletinReference { get; set; } = "BI Nr 113, de 23 JUN 25 da OM EXEMPLO";
    public string SisbolSubject { get; set; } = "Gratificação de Representação";
    public string SisbolSpecificCode { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
    public List<int> SelectedMilitaryIds { get; set; } = [];

    public string RequestNature { get; set; } = "Emprego Operacional relacionado a operação real";
    public string RequestDescription { get; set; } = "Emprego operacional da atividade de escolta de material classe V";
    public string RequestAuthorizingDocument { get; set; } = "DIEx nº 29183-DivOpLog.6/COpLog/COLOG, de 31 OUT 25";
    public string RequestLegalBasis { get; set; } = "Art. 5º, inciso III, alínea a, do Decreto nº 11.002, de 17 MAR 22, e Portaria – C Ex nº 1.887, de 7 DEZ 22 (EB10-N-08.003).";
    public string RequestLocation { get; set; } = "Itajubá - MG";
    public DateTime RequestStartDate { get; set; } = DateTime.Today;
    public string RequestStartTime { get; set; } = "06:00";
    public DateTime RequestEndDate { get; set; } = DateTime.Today;
    public string RequestEndTime { get; set; } = "21:00";
    public string RequestBulletin { get; set; } = "BI Nr 000, de 00/00/0000, da OM EXEMPLO";
    public string RequestContact { get; set; } = "Militar responsável, Furriel da OM EXEMPLO";
    public string RequestRitex { get; set; } = "000-0000";
    public string RequestEmail { get; set; } = "furriel@om-exemplo.mil.br";
    public string RequestAuthority { get; set; } = "NOME DO RESPONSÁVEL - Posto/Função";
    public string RequestOrganization { get; set; } = "OM EXEMPLO";
    public string RequestCity { get; set; } = "Belo Horizonte";
    public int RequestManualDays { get; set; }
    public Dictionary<string, int> EffectiveByRank { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GratificationPeriodInfo
{
    public bool IsValid { get; set; }
    public string Error { get; set; } = string.Empty;
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public TimeSpan Duration { get; set; }
    public int IndemnifiableDays { get; set; }
    public bool ManualDaysOverride { get; set; }
    public string DurationText
    {
        get
        {
            var totalHours = (int)Math.Floor(Duration.TotalHours);
            var days = totalHours / 24;
            var hours = totalHours % 24;
            var minutes = Duration.Minutes;
            var parts = new List<string>();
            if (days > 0) parts.Add($"{days} dia(s)");
            if (hours > 0) parts.Add($"{hours} hora(s)");
            if (minutes > 0) parts.Add($"{minutes} minuto(s)");
            return parts.Count == 0 ? "0 minuto" : string.Join(", ", parts);
        }
    }
    public string RuleText
    {
        get
        {
            if (!IsValid) return Error;
            if (ManualDaysOverride) return $"{IndemnifiableDays} dia(s) informado(s) manualmente; duração real mantida para conferência.";
            var fullDays = (int)Math.Floor(Duration.TotalHours / 24d);
            var remainder = Duration - TimeSpan.FromDays(fullDays);
            return $"{fullDays} período(s) completo(s) de 24h" +
                   (remainder >= TimeSpan.FromHours(8)
                       ? $" e fração residual de {remainder.Hours:00}h{remainder.Minutes:00}, igual ou superior a 8h, computada como mais 1 dia indenizável."
                       : $" e fração residual de {remainder.Hours:00}h{remainder.Minutes:00}, inferior a 8h, não computada como novo dia indenizável.");
        }
    }
}

public sealed class GratificationParticipant
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    public MilitaryRecord Military { get; set; } = new();
    public decimal Salary { get; set; }
    public int Days { get; set; }
    public decimal DailyRate => Salary * 0.02m;
    public decimal Total => DailyRate * Days;
    public string SalaryText => Salary.ToString("C2", PtBr);
    public string DailyRateText => DailyRate.ToString("C2", PtBr);
    public string TotalText => Total.ToString("C2", PtBr);
}

public sealed class GratificationEffectiveRow : INotifyPropertyChanged
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private int _quantity;
    private int _days;

    public string Rank { get; set; } = string.Empty;
    public string ShortRank => Services.MilitaryRankService.ShortName(Rank);
    public decimal Salary { get; set; }
    public int Quantity
    {
        get => _quantity;
        set
        {
            var safe = Math.Max(0, value);
            if (_quantity == safe) return;
            _quantity = safe;
            Notify(nameof(Quantity));
            Notify(nameof(Subtotal));
            Notify(nameof(SubtotalText));
        }
    }
    public int Days
    {
        get => _days;
        set
        {
            if (_days == value) return;
            _days = Math.Max(0, value);
            Notify(nameof(Days));
            Notify(nameof(Subtotal));
            Notify(nameof(SubtotalText));
        }
    }
    public decimal DailyRate => Salary * 0.02m;
    public decimal Subtotal => DailyRate * Days * Quantity;
    public string SalaryText => Salary.ToString("C2", PtBr);
    public string DailyRateText => DailyRate.ToString("C2", PtBr);
    public string SubtotalText => Subtotal.ToString("C2", PtBr);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
