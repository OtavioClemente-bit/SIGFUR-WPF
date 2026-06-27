using SIGFUR.Wpf.Services;

namespace SIGFUR.Wpf.Models;

public sealed class PhpmTemplateDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TemplatePath { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public List<string> Placeholders { get; set; } = [];
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string TypeText
    {
        get
        {
            var extension = Path.GetExtension(TemplatePath).TrimStart('.').ToUpperInvariant();
            return extension.Length > 0 ? extension : "SEM ARQUIVO";
        }
    }
    public string StatusText => File.Exists(TemplatePath) ? "Template disponível" : "Vincule o arquivo do template";
    public string DisplayTitle => IsBuiltIn ? $"★ {Title}" : Title;
}

public sealed class PhpmTemplateCatalog
{
    public List<PhpmTemplateDefinition> Templates { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> SavedValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PhpmFieldItem : INotifyPropertyChanged
{
    private string _value = string.Empty;
    private string _source = "Manual";

    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Source
    {
        get => _source;
        set
        {
            if (_source == value) return;
            _source = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
        }
    }
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PhpmGenerationRecord
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string TemplateTitle { get; set; } = string.Empty;
    public int MilitaryId { get; set; }
    public string MilitaryName { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string SourceDocumentPath { get; set; } = string.Empty;
    public string OutputFormat { get; set; } = "Original";
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string GeneratedAtText => GeneratedAt.ToString("dd/MM/yyyy HH:mm");
    public string FileName => Path.GetFileName(OutputPath);
    public string StatusText => Success ? "Gerado" : "Falhou";
}

public sealed class PhpmGenerationRequest
{
    public required PhpmTemplateDefinition Template { get; init; }
    public required MilitaryRecord Military { get; init; }
    public required IReadOnlyDictionary<string, string> Fields { get; init; }
    public string OutputName { get; init; } = string.Empty;
    public string OutputFormat { get; init; } = "Original"; // Original | DOCX | PDF
    public bool KeepIntermediateDocument { get; init; } = true;
}
