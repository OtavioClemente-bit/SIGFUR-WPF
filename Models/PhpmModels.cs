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
    private string _validationMessage = string.Empty;

    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Group { get; set; } = "Dados complementares";
    public string Hint { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public List<string> Suggestions { get; set; } = [];
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool IsOfficialPublicationReference { get; set; }
    public bool IsMissing => IsRequired && string.IsNullOrWhiteSpace(Value);
    public string ValidationMessage
    {
        get => _validationMessage;
        set
        {
            if (_validationMessage == value) return;
            _validationMessage = value ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValidationMessage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ValidationMessage);
    public string RequirementText => IsRequired ? "Obrigatório" : "Opcional";
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
            _source = IsOfficialPublicationReference && Suggestions.Contains(_value, StringComparer.CurrentCultureIgnoreCase)
                ? "Biblioteca"
                : "Manual";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMissing)));
        }
    }

    public void SetValue(string? value, string source)
    {
        _value = value ?? string.Empty;
        _source = source ?? string.Empty;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMissing)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PhpmPublicationReferenceCatalog
{
    public List<string> Bulletins { get; set; } = [];
    public List<string> Aditaments { get; set; } = [];
}

public sealed class PhpmBatchItem : INotifyPropertyChanged
{
    private int _pendingFields;
    public required MilitaryRecord Military { get; init; }
    public int Id => Military.Id;
    public string ShortRank => Military.ShortRank;
    public string Name => Military.Name;
    public string WarName => Military.WarName;
    public string PrecCp => Military.PrecCp;
    public int PendingFields
    {
        get => _pendingFields;
        set
        {
            if (_pendingFields == value) return;
            _pendingFields = Math.Max(0, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingFields)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }
    public string StatusText => PendingFields == 0 ? "Pronto" : $"{PendingFields} pendência(s)";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class PhpmFormConfiguration
{
    public bool IncludeSpouse { get; set; }
    public int ChildrenCount { get; set; }
    public bool IncludeCompanion { get; set; }
    public int OtherBeneficiariesCount { get; set; }
    public bool IncludeDesignatedPerson { get; set; }
    public int DependentsCount { get; set; }
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
