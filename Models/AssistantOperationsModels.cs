namespace SIGFUR.Wpf.Models;

public sealed class AssistantOperationalItem
{
    public string Id { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public DateTime? DueDate { get; init; }
    public bool Urgent { get; init; }
    public string DateLabel => DueDate?.ToString("dd/MM/yyyy") ?? "Não cadastrado";
    public string Status => DueDate?.Date < DateTime.Today ? "Atrasado" : DueDate?.Date == DateTime.Today ? "Hoje" : Urgent ? "Urgente" : "Pendente";
}

public sealed class AssistantOperationalSnapshot
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public List<AssistantOperationalItem> Items { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
