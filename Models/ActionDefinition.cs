namespace SIGFUR.Wpf.Models;

public sealed class ActionDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Icon { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public string HotKey { get; set; } = string.Empty;
    public bool IsNative { get; init; }
}

public sealed class NavigationGroup
{
    public required string Title { get; init; }
    public List<ActionDefinition> Actions { get; init; } = [];
}
