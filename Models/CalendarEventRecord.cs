namespace SIGFUR.Wpf.Models;

public sealed class CalendarEventRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Date { get; set; } = string.Empty; // yyyy-MM-dd
    public string Type { get; set; } = "Evento";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
