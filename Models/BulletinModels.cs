using System.Collections.ObjectModel;

namespace SIGFUR.Wpf.Models;

public sealed class BulletinTemplate : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _text = string.Empty;
    private int _order;

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value ?? string.Empty; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }

    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value ?? string.Empty; PropertyChanged?.Invoke(this, new(nameof(Text))); }
    }

    public int Order
    {
        get => _order;
        set { if (_order == value) return; _order = value; PropertyChanged?.Invoke(this, new(nameof(Order))); }
    }

    public bool IsBuiltIn { get; set; }
    public string SearchText => $"{Name} {Text}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public BulletinTemplate Clone(string? newName = null) => new() { Name = newName ?? Name, Text = Text, Order = Order, IsBuiltIn = false };
}

public sealed class BulletinSelectedMilitary : INotifyPropertyChanged
{
    private int _position;
    public required MilitaryRecord Military { get; init; }
    public int Position
    {
        get => _position;
        set { if (_position == value) return; _position = value; PropertyChanged?.Invoke(this, new(nameof(Position))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class BulletinFieldDefinition
{
    public string Key { get; set; } = string.Empty;
    public string DisplayKey { get; set; } = string.Empty;
    public string Type { get; set; } = "text";
    public string Meta { get; set; } = string.Empty;
    public string DateFormat { get; set; } = "BR";
    public string MoneyFormat { get; set; } = "NUMERO";
    public string MonthFormat { get; set; } = "MES";
    public List<string> Options { get; set; } = [];
}

public sealed class BulletinPreferences
{
    public string LastTemplate { get; set; } = string.Empty;
    public string AvailableSortMode { get; set; } = "Ordem salva (Listar Militares)";
    public List<string> TemplateOrder { get; set; } = [];
    public bool OrderLocked { get; set; }
    public List<int> SelectedMilitaryIds { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> FormValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<int>> SelectionByTemplate { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BulletinRenderResult
{
    public string Text { get; set; } = string.Empty;
    public List<BulletinBoldRange> BoldRanges { get; set; } = [];
    public List<string> UnresolvedTokens { get; set; } = [];
}

public readonly record struct BulletinBoldRange(int Start, int Length);

public sealed class BulletinKnowledgeCatalog
{
    public string Version { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<BulletinKnowledgeRule> Rules { get; set; } = [];
}

public sealed class BulletinKnowledgeRule
{
    public string Id { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = [];
    public List<BulletinKnowledgeField> RequiredFields { get; set; } = [];
    public List<BulletinTextRequirement> RequiredText { get; set; } = [];
    public List<string> Checklist { get; set; } = [];
    public List<string> RelatedPublications { get; set; } = [];
    public List<string> AiGuidance { get; set; } = [];
    public List<string> SourceReferences { get; set; } = [];
    public string ExamplePattern { get; set; } = string.Empty;
    public string SearchText => string.Join(' ', new[] { Id, TemplateName, Title, Category, Action, Summary }
        .Concat(Aliases).Concat(Checklist).Concat(AiGuidance));
}

public sealed class BulletinKnowledgeField
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Source { get; set; } = "form";
    public string Description { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
    public List<string> Options { get; set; } = [];
}

public sealed class BulletinTextRequirement
{
    public string Label { get; set; } = string.Empty;
    public string Severity { get; set; } = "error";
    public List<string> AnyOf { get; set; } = [];
}

public sealed class BulletinComplianceReport
{
    public BulletinKnowledgeRule? Rule { get; set; }
    public int Score { get; set; }
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Recommendations { get; set; } = [];
    public List<string> Passed { get; set; } = [];
    public bool IsBlocked => Errors.Count > 0;
}

public sealed class SisbolSettings
{
    public string Browser { get; set; } = "edge";
    public string Login { get; set; } = string.Empty;
    public bool SavePassword { get; set; }
    public string ProtectedPassword { get; set; } = string.Empty;
    public bool HideAfterLogin { get; set; } = true;
    public bool IncludeAutomatically { get; set; } = true;
}
