using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace SIGFUR.Wpf.Models;

public sealed class ReminderRecord : INotifyPropertyChanged
{
    private int _id;
    private string _title = string.Empty;
    private string _date = string.Empty;
    private string _body = string.Empty;
    private bool _completed;
    private string _priority = "Normal";
    private string _recurrence = "Nenhuma";
    private bool _autoReschedule = true;
    private string _createdAt = string.Empty;
    private string _archivedAt = string.Empty;

    [JsonPropertyName("id")] public int Id { get => _id; set => Set(ref _id, value); }
    [JsonPropertyName("titulo")] public string Title { get => _title; set => Set(ref _title, value ?? string.Empty); }
    [JsonPropertyName("data")] public string Date { get => _date; set { if (Set(ref _date, value ?? string.Empty)) NotifyCalculated(); } }
    [JsonPropertyName("corpo")] public string Body { get => _body; set => Set(ref _body, value ?? string.Empty); }
    [JsonPropertyName("concluido")] public bool Completed { get => _completed; set { if (Set(ref _completed, value)) NotifyCalculated(); } }
    [JsonPropertyName("prioridade")] public string Priority { get => _priority; set { if (Set(ref _priority, value ?? "Normal")) NotifyCalculated(); } }
    [JsonPropertyName("recorrencia")] public string Recurrence { get => _recurrence; set => Set(ref _recurrence, value ?? "Nenhuma"); }
    [JsonPropertyName("auto_reagendar")] public bool AutoReschedule { get => _autoReschedule; set => Set(ref _autoReschedule, value); }
    [JsonPropertyName("criado_em")] public string CreatedAt { get => _createdAt; set => Set(ref _createdAt, value ?? string.Empty); }
    [JsonPropertyName("arquivado_em")] public string ArchivedAt { get => _archivedAt; set => Set(ref _archivedAt, value ?? string.Empty); }

    [JsonIgnore] public DateTime? DueDate => ReminderDate.Parse(Date);
    [JsonIgnore] public string FormattedDate => DueDate?.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("pt-BR")) ?? "—";
    [JsonIgnore] public int? DaysRemaining => DueDate is null ? null : (DueDate.Value.Date - DateTime.Today).Days;
    [JsonIgnore] public string DaysText => DaysRemaining switch
    {
        null => "—",
        0 => "Hoje",
        > 0 => $"{DaysRemaining} dia(s)",
        _ => $"{Math.Abs(DaysRemaining.Value)} dia(s) em atraso"
    };
    [JsonIgnore] public string Status => Completed ? "Concluído" : DueDate is null ? "Sem data" : DaysRemaining < 0 ? "Atrasado" : DaysRemaining == 0 ? "Hoje" : "Pendente";
    [JsonIgnore] public string EffectivePriority => ReminderRules.AutoPriority(DueDate, Priority);
    [JsonIgnore] public string CompletionText => Completed ? "Sim" : "Não";
    [JsonIgnore] public string Summary => string.IsNullOrWhiteSpace(Body) ? Title : $"{Title}\n{Body}";

    public ReminderRecord Clone() => new()
    {
        Id = Id, Title = Title, Date = Date, Body = Body, Completed = Completed,
        Priority = Priority, Recurrence = Recurrence, AutoReschedule = AutoReschedule,
        CreatedAt = CreatedAt, ArchivedAt = ArchivedAt
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
    private void NotifyCalculated()
    {
        foreach (var name in new[] { nameof(DueDate), nameof(FormattedDate), nameof(DaysRemaining), nameof(DaysText), nameof(Status), nameof(EffectivePriority), nameof(CompletionText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class ReminderSettings
{
    public string Search { get; set; } = string.Empty;
    public string Order { get; set; } = "Data (mais próximo)";
    public int UpcomingDays { get; set; } = 2;
    public bool ShowCompleted { get; set; } = true;
    public bool GroupByPriority { get; set; } = true;
    public bool AutoClassify { get; set; } = true;
}

public sealed class ReminderSummary
{
    public int Overdue { get; init; }
    public int Today { get; init; }
    public int Upcoming { get; init; }
    public int NoDate { get; init; }
    public int Completed { get; init; }
    public int Total { get; init; }
}

public static class ReminderDate
{
    public static DateTime? Parse(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text == "—") return null;
        string[] formats = ["dd/MM/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy", "dd/MM/yy"];
        foreach (var format in formats)
            if (DateTime.TryParseExact(text, format, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var parsed)) return parsed.Date;
        return DateTime.TryParse(text, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out var fallback) ? fallback.Date : null;
    }
}

public static class ReminderRules
{
    public static IReadOnlyList<string> Priorities { get; } = ["Urgentíssimo", "Urgente", "Normal", "Baixa"];
    public static IReadOnlyList<string> Recurrences { get; } = ["Nenhuma", "Diária", "Semanal", "Mensal", "Anual"];

    public static string AutoPriority(DateTime? dueDate, string? basePriority)
    {
        var priority = string.IsNullOrWhiteSpace(basePriority) ? "Normal" : basePriority.Trim();
        if (priority.Equals("Urgentíssimo", StringComparison.OrdinalIgnoreCase)) return "Urgentíssimo";
        if (dueDate is null) return priority;
        var days = (dueDate.Value.Date - DateTime.Today).Days;
        if (days <= 0) return "Urgentíssimo";
        if (days <= 2) return "Urgente";
        return priority;
    }

    public static DateTime NextDate(DateTime date, string recurrence) => recurrence switch
    {
        "Diária" => date.AddDays(1),
        "Semanal" => date.AddDays(7),
        "Mensal" => AddMonthClamped(date),
        "Anual" => AddYearClamped(date),
        _ => date
    };

    private static DateTime AddMonthClamped(DateTime date)
    {
        var first = new DateTime(date.Year, date.Month, 1).AddMonths(1);
        return new DateTime(first.Year, first.Month, Math.Min(date.Day, DateTime.DaysInMonth(first.Year, first.Month)));
    }

    private static DateTime AddYearClamped(DateTime date)
    {
        var year = date.Year + 1;
        return new DateTime(year, date.Month, Math.Min(date.Day, DateTime.DaysInMonth(year, date.Month)));
    }
}
