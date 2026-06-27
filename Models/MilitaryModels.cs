using System.Collections.ObjectModel;
using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class MilitaryRecord : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string Rank { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string WarName { get; set; } = string.Empty;

    // Compatibilidade com versões anteriores do XAML.
    // O setter evita falha fatal caso um BAML antigo tente usar ligação TwoWay.
    public string NomeDeGuerraView
    {
        get => WarName;
        set => WarName = value ?? string.Empty;
    }
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
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsFavorite { get; set; }
    public bool IsAttached { get; set; }
    public string Annotation { get; set; } = string.Empty;
    public string CustomColor { get; set; } = string.Empty;
    public string RowColor => string.IsNullOrWhiteSpace(CustomColor)
        ? MilitaryRankService.GetAutomaticRowColor(Rank, FormationYear)
        : CustomColor;
    public string RowColorDescription => MilitaryRankService.GetColorDescription(Rank, FormationYear);
    public bool IsOrange { get; set; }
    public string Education { get; set; } = string.Empty;
    private bool _isMarkedForBatch;
    /// <summary>Marcação persistente para ações em lote; permanece ativa mesmo quando filtros ocultam a linha.</summary>
    public bool IsMarkedForBatch
    {
        get => _isMarkedForBatch;
        set
        {
            if (_isMarkedForBatch == value) return;
            _isMarkedForBatch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMarkedForBatch)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ShortRank => MilitaryRankService.ShortName(Rank);
    public string FormattedCpf => MilitaryFormatting.FormatCpf(Cpf);
    public string DisplayName => string.IsNullOrWhiteSpace(WarName) ? Name : $"{Name} — {WarName}";
    public string NameBeforeWar => SplitDisplayName().Before;
    public string NameWarBold => SplitDisplayName().War;
    public string NameAfterWar => SplitDisplayName().After;
    public int? CalculatedServiceTimeDays { get; set; }
    public string CalculatedServiceTimeText { get; set; } = string.Empty;
    public int ServiceTimeDays => CalculatedServiceTimeDays ?? (EnlistmentDateValue is null ? 0 : Math.Max(0, (DateTime.Today - EnlistmentDateValue.Value.Date).Days + 1));
    public string ServiceTimeText => !string.IsNullOrWhiteSpace(CalculatedServiceTimeText)
        ? CalculatedServiceTimeText
        : MilitaryFormatting.FormatServiceTime(EnlistmentDateValue, DateTime.Today);
    public DateTime? EnlistmentDateValue => MilitaryFormatting.ParseDate(EnlistmentDate);
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string TransportStatus => IsAttached ? "Bloqueado" : IsYes(ReceivesTransportAid) ? "Recebe" : "Não recebe";
    public string PnrStatus => IsYes(HasPnr) ? "Sim" : "Não";
    public string PhotoStatus => string.IsNullOrWhiteSpace(PhotoPath) ? "Sem foto" : "Foto cadastrada";

    public MilitaryRecord Clone() => (MilitaryRecord)MemberwiseClone();

    private (string Before, string War, string After) SplitDisplayName()
    {
        var fullName = (Name ?? string.Empty).Trim();
        var warName = (WarName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(fullName)) return (string.Empty, warName, string.Empty);
        if (string.IsNullOrWhiteSpace(warName)) return (fullName, string.Empty, string.Empty);
        var index = fullName.IndexOf(warName, StringComparison.CurrentCultureIgnoreCase);
        if (index >= 0)
            return (fullName[..index], fullName.Substring(index, warName.Length), fullName[(index + warName.Length)..]);
        return (fullName + " — ", warName, string.Empty);
    }

    public static bool IsYes(string? value)
        => string.Equals(value?.Trim(), "Sim", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value?.Trim(), "S", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase)
           || value?.Trim() == "1";
}

public sealed class MilitaryDocumentRecord
{
    public int Id { get; set; }
    public int MilitaryId { get; set; }
    public string Type { get; set; } = "DOCUMENTO";
    public string Title { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SavedAt { get; set; } = string.Empty;
    public string Observation { get; set; } = string.Empty;
    public string KeysJson { get; set; } = string.Empty;
    public bool Exists => !string.IsNullOrWhiteSpace(Path) && File.Exists(Path);
    public string Status => Exists ? "Disponível" : "Arquivo não encontrado";
    public bool HasOcrKeys => !string.IsNullOrWhiteSpace(KeysJson);
    public string OcrStatus => Type.Equals("CERTIDAO_NASCIMENTO", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("PENSAO_JUDICIAL", StringComparison.OrdinalIgnoreCase)
        ? (HasOcrKeys ? "OCR/chaves salvos" : "OCR pendente")
        : "—";
}

public sealed class TransportFareRecord
{
    public int Index { get; set; }
    public string Number { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public double Fare { get; set; }
    public string Description
    {
        get
        {
            var identity = string.Join(" — ", new[] { Number, Name }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return string.IsNullOrWhiteSpace(identity) ? $"Ônibus {Index + 1}" : identity;
        }
    }
    public string DisplayIndex => (Index + 1).ToString(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
    public string FormattedFare => MilitaryFormatting.FormatMoney(Fare);
    public string RoundTripFormatted => MilitaryFormatting.FormatMoney(Fare * 2.0);
}

public sealed class TransportSummary
{
    public ObservableCollection<TransportFareRecord> Fares { get; } = [];
    public double Salary { get; set; }
    public int WorkingDays { get; set; } = 22;
    public bool BlockedByAttachedStatus { get; set; }
    public double StoredGrossPerMonth { get; set; }

    /// <summary>Valor líquido mensal salvo em militares.valor_aux_transporte.
    /// Mantém compatibilidade com o módulo Python: o valor cadastrado é a fonte oficial exibida na carteira,
    /// e as linhas/tarifas servem para memória de cálculo.</summary>
    public double StoredNetPerMonth { get; set; }
    public bool HasStoredNetPerMonth => StoredNetPerMonth > 0.004;

    public double GrossPerDay => Fares.Count > 0
        ? Fares.Sum(x => x.Fare) * 2.0
        : WorkingDays > 0 ? StoredGrossPerMonth / WorkingDays : 0;
    public double GrossPerMonth => Fares.Count > 0
        ? GrossPerDay * Math.Max(0, WorkingDays)
        : Math.Max(0, StoredGrossPerMonth);
    public double Share => Salary > 0 && WorkingDays > 0 ? Salary * 0.06 * (WorkingDays / 30.0) : 0;
    public double CalculatedNetPerMonth => Math.Max(0, GrossPerMonth - Share);
    public double NetPerMonth => BlockedByAttachedStatus ? 0 : HasStoredNetPerMonth ? StoredNetPerMonth : CalculatedNetPerMonth;
    public double NetPerDay => WorkingDays > 0 ? NetPerMonth / WorkingDays : 0;
    public string Status => BlockedByAttachedStatus
        ? "Bloqueado por Adido/Encostado"
        : Fares.Count == 0 && HasStoredNetPerMonth
            ? "Valor líquido cadastrado no banco; linhas não detalhadas"
            : Fares.Count == 0
                ? "Sem tarifas cadastradas"
                : HasStoredNetPerMonth
                    ? "Linhas cadastradas; líquido oficial do banco"
                    : "Cálculo disponível";
    public string SalaryText => MilitaryFormatting.FormatMoney(Salary);
    public string GrossPerDayText => MilitaryFormatting.FormatMoney(GrossPerDay);
    public string GrossPerMonthText => MilitaryFormatting.FormatMoney(GrossPerMonth);
    public string ShareText => MilitaryFormatting.FormatMoney(Share);
    public string NetPerMonthText => MilitaryFormatting.FormatMoney(NetPerMonth);
    public string NetPerDayText => $"Líquido por dia: {MilitaryFormatting.FormatMoney(NetPerDay)}";
}

public sealed class ServiceIntervalRecord
{
    public int Id { get; set; }
    public int MilitaryId { get; set; }
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public string Observation { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Active { get; set; } = true;
    public int Days => MilitaryFormatting.IntervalDays(StartDate, EndDate);
    public string FormattedDays => $"{Days:N0} dia(s)";
}

public sealed class PaystubFileRecord
{
    public string Path { get; set; } = string.Empty;
    public string FileName => System.IO.Path.GetFileName(Path);
    public DateTime ModifiedAt { get; set; }
    public long SizeBytes { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string DocumentType { get; set; } = "Contracheque";
    public bool IsFinancialStatement => DocumentType.Equals("Ficha Financeira", StringComparison.OrdinalIgnoreCase);
    public string SizeText => MilitaryFormatting.FormatFileSize(SizeBytes);
}


public sealed class MilitaryTrashEntry
{
    public int Index { get; set; }
    public DateTime DeletedAt { get; set; }
    public MilitaryRecord Record { get; set; } = new();
    public string DeletedAtText => DeletedAt == default ? "—" : DeletedAt.ToString("dd/MM/yyyy HH:mm");
    public string MilitaryText => $"{Record.ShortRank} {Record.Name}".Trim();
}

public sealed class MilitaryListSettings
{
    public string Search { get; set; } = string.Empty;
    public string Rank { get; set; } = "Todos";
    public string Year { get; set; } = "Todos";
    public bool FavoritesOnly { get; set; }
    public bool AttachedOnly { get; set; }
    public bool MissingTransportOnly { get; set; }
    public bool OrangeOnly { get; set; }
    public bool OrderLocked { get; set; }
    public string SortMode { get; set; } = "Hierarquia do Exército";
    public List<string> VisibleColumns { get; set; } = [];
    public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<int> CustomOrder { get; set; } = [];
}



public sealed class PersonnelRelationPreferences
{
    public string SortMode { get; set; } = "Hierarquia do Exército";
    public bool OpenAfterGenerate { get; set; } = true;
    public List<int> OrderedMilitaryIds { get; set; } = [];
}

public sealed class MilitarySavedList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<int> OrderedMilitaryIds { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string DisplayLabel => $"{Name} ({OrderedMilitaryIds.Count})";
}

public sealed class MilitarySavedListStore
{
    public List<MilitarySavedList> Lists { get; set; } = [];
    public string LastOpenedListId { get; set; } = string.Empty;
}

public sealed class DocumentFormProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string DisplayLabel => Name;
}

public sealed class DocumentProfileStore
{
    public Dictionary<string, List<DocumentFormProfile>> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MilitaryColumnOption
{
    public string Key { get; set; } = string.Empty;
    public string Header { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool IsRequired { get; set; }
    public int Order { get; set; }
}

public sealed class BusRouteRecord
{
    public int Index { get; set; }
    public string Number { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public double Fare { get; set; }
    public string DisplayName => string.Join(" — ", new[] { Number, Description }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string FormattedFare => MilitaryFormatting.FormatMoney(Fare);
}

public sealed class TransportRouteDetails
{
    public string Origin { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public string RouteDescription { get; set; } = string.Empty;
    public List<BusRouteRecord> Buses { get; set; } = [];
}

public enum GeneratedDocumentType
{
    TransportAid,
    PecuniaryCompensation,
    AuthenticPaymentCopy,
    AdvanceChristmasBonus,
    PostalLabel,
    CoverSheet,
    ExercisePreviousRequest,
    JudicialPensionWorksheet,
    RemissiveIndex,
    GratificationDiex,
    GratificationMap
}

public sealed class DocumentGenerationRequest
{
    public GeneratedDocumentType Type { get; set; }
    public IReadOnlyList<MilitaryRecord> Military { get; set; } = [];
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string OutputDirectory { get; set; } = string.Empty;
    public string TemplatePath { get; set; } = string.Empty;
    public bool OpenAfterGenerate { get; set; } = true;
    /// <summary>Preenche o endereço residencial da SAT a partir do cadastro/rota do militar.</summary>
    public bool UseTransportAddressFromDatabase { get; set; } = true;
    /// <summary>Preenche ônibus, tarifas e totais da SAT a partir do banco de Auxílio-Transporte.</summary>
    public bool UseTransportBusesFromDatabase { get; set; } = true;
    public bool PrintAfterGenerate { get; set; }
    public string PrinterName { get; set; } = string.Empty;
}

public sealed class DocumentGenerationResult
{
    public List<string> Files { get; } = [];
    public List<string> Failures { get; } = [];
}

public static class MilitaryFormatting
{
    private static readonly Dictionary<string, string> RankMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["General de Exército"] = "Gen Ex", ["General de Divisão"] = "Gen Div", ["General de Brigada"] = "Gen Bda",
        ["Coronel"] = "Cel", ["Tenente Coronel"] = "Ten Cel", ["Major"] = "Maj", ["Capitão"] = "Cap",
        ["1º Tenente"] = "1º Ten", ["2º Tenente"] = "2º Ten", ["Aspirante"] = "Asp", ["Subtenente"] = "S Ten",
        ["1º Sargento"] = "1º Sgt", ["2º Sargento"] = "2º Sgt", ["3º Sargento"] = "3º Sgt",
        ["Cabo Efetivo Profissional"] = "Cb Ef Profl", ["Cabo"] = "Cb Ef Profl", ["Cb"] = "Cb Ef Profl", ["Cb Ef Vrv"] = "Cb Ef Profl",
        ["Soldado Efetivo Profissional"] = "Sd Ef Profl", ["Soldado"] = "Sd Ef Profl", ["Sd"] = "Sd Ef Profl", ["Soldado Antigo"] = "Sd Ef Profl", ["Sd Antigo"] = "Sd Ef Profl",
        ["Soldado Efetivo Variável"] = "Sd Ef Vrv", ["Soldado Recruta"] = "Sd Ef Vrv", ["Sd Rcr"] = "Sd Ef Vrv", ["Sd EV"] = "Sd Ef Vrv", ["Recruta"] = "Sd Ef Vrv",
        ["Sd Ef Profl"] = "Sd Ef Profl", ["Sd Ef Vrv"] = "Sd Ef Vrv"
    };

    public static string ShortRank(string? value) => MilitaryRankService.ShortName(value);

    public static string Digits(string? value) => new((value ?? string.Empty).Where(char.IsDigit).ToArray());

    public static string FormatCpf(string? value)
    {
        var digits = Digits(value);
        return digits.Length == 11 ? $"{digits[..3]}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits[9..]}" : value ?? string.Empty;
    }

    public static string FormatMoney(double value) => value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().Trim('\uFEFF', '"', '\'');
        if (string.IsNullOrWhiteSpace(text)) return null;

        var formats = new[]
        {
            "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy",
            "yyyy-MM-dd", "yyyy/M/d", "dd-MM-yyyy", "d-M-yyyy",
            "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
            "dd/MM/yyyy HH:mm:ss", "d/M/yyyy H:mm:ss"
        };
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("pt-BR"), CultureInfo.InvariantCulture })
            if (DateTime.TryParseExact(text, formats, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var exact))
                return exact.Date;

        var br = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)(\d{1,2})[./-](\d{1,2})[./-](\d{2,4})(?!\d)");
        if (br.Success)
        {
            var day = int.Parse(br.Groups[1].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(br.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = int.Parse(br.Groups[3].Value, CultureInfo.InvariantCulture);
            if (year < 100) year += year >= 70 ? 1900 : 2000;
            try { return new DateTime(year, month, day); } catch { }
        }

        var iso = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)(\d{4})[./-](\d{1,2})[./-](\d{1,2})(?!\d)");
        if (iso.Success)
        {
            try
            {
                return new DateTime(
                    int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(iso.Groups[3].Value, CultureInfo.InvariantCulture));
            }
            catch { }
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is >= 20000 and <= 80000)
        {
            try { return DateTime.FromOADate(serial).Date; } catch { }
        }

        foreach (var culture in new[] { CultureInfo.GetCultureInfo("pt-BR"), CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-US") })
            if (DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var parsed))
                return parsed.Date;
        return null;
    }

    public static string NormalizeDateText(string? value)
        => ParseDate(value)?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? (value ?? string.Empty).Trim();

    public static int IntervalDays(string? start, string? end)
    {
        var s = ParseDate(start);
        if (s is null) return 0;
        var e = ParseDate(end) ?? DateTime.Today;
        return Math.Max(0, (e.Date - s.Value.Date).Days + 1);
    }

    public static string FormatServiceTime(DateTime? start, DateTime? end = null)
    {
        if (start is null) return "Data de praça não informada";
        var finish = (end ?? DateTime.Today).Date;
        var begin = start.Value.Date;
        if (finish < begin) return "Data de praça inválida";
        var years = finish.Year - begin.Year;
        var months = finish.Month - begin.Month;
        var days = finish.Day - begin.Day;
        if (days < 0)
        {
            var previousMonth = finish.AddMonths(-1);
            days += DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month);
            months--;
        }
        if (months < 0) { months += 12; years--; }
        var totalDays = (finish - begin).Days + 1;
        return $"{Math.Max(0, years)}a, {Math.Max(0, months):00}m e {Math.Max(0, days):00}d ({totalDays:N0} dias)";
    }

    public static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }
}

public sealed class CopyFormatSettings
{
    public string Template { get; set; } = "{PG} {NOME}\nPrec-CP {PREC} CPF {CPF}";
}

public sealed class MilitaryExportPreferences
{
    public List<string> SelectedColumns { get; set; } = [];
    public string Format { get; set; } = "Excel (.xlsx)";
    public bool UseSelectedOnly { get; set; } = true;
}

public sealed class PostalOmAddress
{
    public string OmName { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public string Complement { get; set; } = string.Empty;
    public string Neighborhood { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public bool ImportedOnline { get; set; }
    public string DisplayLabel => string.IsNullOrWhiteSpace(City) ? OmName : $"{OmName} — {City}/{State}";
    public string FullAddress
    {
        get
        {
            var line = string.Join(", ", new[] { Street, Number }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (!string.IsNullOrWhiteSpace(Complement)) line += $" — {Complement}";
            if (!string.IsNullOrWhiteSpace(Neighborhood)) line += $" — {Neighborhood}";
            return line.Trim(' ', '—', ',');
        }
    }
}
